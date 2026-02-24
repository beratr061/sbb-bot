using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using SbbBot.Helpers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SbbBot.Services;

public class FareWatcherService : BackgroundService
{
    private readonly ILogger<FareWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;

    // We will check a representative line to detect general fare hikes.
    // Line 137 (A1/Sakarya) is used as reference based on user's sample.
    // We could check multiple if needed, but usually hikes are city-wide.
    private const int ReferenceLineId = 137; 
    private const string ApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/line-fare/{0}?busType=3869";
    private const string StateFile = "Data/fare_state.json";

    public FareWatcherService(
        ILogger<FareWatcherService> logger,
        IHttpClientFactory httpClientFactory,
        TelegramHelper telegramHelper,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _telegramHelper = telegramHelper;
        _config = config.Value;

        _retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(5),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning($"FareWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FareWatcherService started.");

        int intervalMinutes = _config.Intervals.FareMinutes > 0 ? _config.Intervals.FareMinutes : 1440; // Default daily
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        do
        {
            try
            {
                await CheckFaresAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in FareWatcherService execution");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }

    private async Task CheckFaresAsync(CancellationToken cancellationToken)
    {
        int intervalMinutes = _config.Intervals.FareMinutes > 0 ? _config.Intervals.FareMinutes : 1440;
        var ageMinutes = StorageHelper.GetDataFileAgeMinutes("fare_state.json");
        if (ageMinutes < intervalMinutes)
        {
            _logger.LogInformation("Fare data is fresh ({0:F0}m old), skipping check.", ageMinutes);
            return;
        }

        var fareData = await FetchFareDataAsync(ReferenceLineId, cancellationToken);
        if (fareData == null) return;

        var storedHash = await StorageHelper.ReadStateAsync(StateFile);
        var currentHash = ComputeFareHash(fareData);

        if (storedHash != currentHash)
        {
            if (!string.IsNullOrEmpty(storedHash))
            {
                // Parse and notify
                await NotifyFareChangeAsync(fareData);
            }
            else
            {
                 _logger.LogInformation("Initial fare data stored.");
            }

            await StorageHelper.SaveStateAsync(StateFile, currentHash);
        }

        // Mark as checked (touch file even if no change)
        StorageHelper.TouchDataFile("fare_state.json");
    }

    private async Task<JsonElement?> FetchFareDataAsync(int lineId, CancellationToken token)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = string.Format(ApiUrl, lineId);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
            request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await _retryPolicy.ExecuteAsync(async () => 
                await client.SendAsync(request, token));

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone(); // Clone to detach from disposable doc
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching fare data for line {lineId}");
            return null;
        }
    }

    private string ComputeFareHash(JsonElement? root)
    {
        if (root == null) return "";
        // Simple hash of the JSON string
        // For more precision, we could just extract prices.
        // Let's rely on string content as order usually stable from API.
        var raw = root.Value.GetRawText();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToBase64String(bytes);
    }

    private async Task NotifyFareChangeAsync(JsonElement? root)
    {
        if (root == null) return;
        var r = root.Value;
        
        // Extract meaningful info to show user
        // We will look for "groups" -> "routes" -> "tariffs"
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("💰 *Ulaşım Ücret Tarifesinde Değişiklik*");
        sb.AppendLine();

        if (r.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                var groupName = group.GetProperty("name").GetString();
                sb.AppendLine($"📍 *{groupName}*");

                if (group.TryGetProperty("routes", out var routes))
                {
                    foreach (var route in routes.EnumerateArray())
                    {
                        var routeName = route.GetProperty("routeName").GetString();
                        var baseFare = route.GetProperty("baseFare").GetDecimal();
                        
                        sb.AppendLine($"   🔹 {routeName}: ~{baseFare} TL~");

                        if (route.TryGetProperty("tariffs", out var tariffs))
                        {
                            foreach (var tariff in tariffs.EnumerateArray())
                            {
                                // We need "tariffList" from root to map IDs if needed, 
                                // but actually tariffs array has details? No, it has finalFare.
                                // The tariffList at root has names (Tam, Ogrenci).
                                var finalFare = tariff.GetProperty("finalFare").GetDecimal();
                                var typeId = tariff.GetProperty("lineFareTypeId").GetInt32();
                                var typeName = GetTariffName(r, typeId);

                                sb.AppendLine($"      🔸 {typeName}: *{finalFare} TL*");
                            }
                        }
                        sb.AppendLine();
                    }
                }
            }
        }
        
        sb.AppendLine("ℹ️ _Fiyatlar örnek hat (A1) baz alınarak tespit edilmiştir. Tüm hatlarda benzer artış olabilir._");
        
        await _telegramHelper.SendMessageAsync(sb.ToString());
    }

    private string GetTariffName(JsonElement root, int typeId)
    {
        // "tariffList":[{"lineFareTypeId":38,"typeName":"Tam"...}]
        if (root.TryGetProperty("tariffList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.GetProperty("lineFareTypeId").GetInt32() == typeId)
                {
                    return item.GetProperty("typeName").GetString() ?? "Diğer";
                }
            }
        }
        return "Diğer";
    }
}
