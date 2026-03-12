using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using SbbBot.Helpers;
using SbbBot.Models;
using SbbBot.Repositories;
using System.Text;
using System.Text.Json;

namespace SbbBot.Services;

public class FareWatcherService : BackgroundService
{
    private readonly ILogger<FareWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly IDiscordHelper _discordHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly FareRepository _repository;
    private readonly BusLineRepository _busLineRepository;

    private const string FareApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/line-fare/{0}?busType=3869";

    public FareWatcherService(
        ILogger<FareWatcherService> logger,
        IHttpClientFactory httpClientFactory,
        TelegramHelper telegramHelper,
        IDiscordHelper discordHelper,
        IOptions<BotConfig> config,
        FareRepository repository,
        BusLineRepository busLineRepository)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _telegramHelper = telegramHelper;
        _discordHelper = discordHelper;
        _config = config.Value;
        _repository = repository;
        _busLineRepository = busLineRepository;

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

        int intervalMinutes = _config.Intervals.FareMinutes > 0 ? _config.Intervals.FareMinutes : 1440;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        do
        {
            try
            {
                await CheckAllFaresAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in FareWatcherService execution");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }

    private async Task CheckAllFaresAsync(CancellationToken cancellationToken)
    {
        bool isFirstLoad = !await _repository.IsSeededAsync();

        // Get all lines from DB
        var allLines = await _busLineRepository.GetAllAsync();
        if (allLines.Count == 0)
        {
            _logger.LogWarning("FareWatcherService: No bus lines in DB yet. Skipping fare check.");
            return;
        }

        _logger.LogInformation("FareWatcherService: Checking fares for {Count} lines...", allLines.Count);

        int processedCount = 0;
        int changedCount = 0;

        foreach (var line in allLines)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Use ApiId for API call; skip if no ApiId
            if (line.ApiId == 0)
            {
                continue;
            }

            try
            {
                bool changed = await ProcessLineFareAsync(line, isFirstLoad, cancellationToken);
                if (changed) changedCount++;
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing fare for line {LineNumber}", line.LineNumber);
            }

            // Delay between API calls to avoid rate limiting
            await Task.Delay(500, cancellationToken);
        }

        if (isFirstLoad)
        {
            await _repository.MarkAsSeededAsync();
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı ({Count} hat), bildirim atlanıyor.", 
                nameof(FareWatcherService), processedCount);
        }
        else
        {
            _logger.LogInformation("FareWatcherService: {Processed} hat kontrol edildi, {Changed} değişiklik tespit edildi.", 
                processedCount, changedCount);
        }
    }

    /// <summary>
    /// Fetches fare for a single line, compares with stored data, and sends notification if changed.
    /// Returns true if fare changed.
    /// </summary>
    private async Task<bool> ProcessLineFareAsync(BusLine line, bool isFirstLoad, CancellationToken cancellationToken)
    {
        var fareData = await FetchFareDataAsync(line.ApiId, cancellationToken);
        if (fareData == null) return false;

        var r = fareData.Value;
        var (fullFare, studentFare, discountedFare) = ExtractFares(r);

        // Skip if no fare data returned
        if (fullFare == 0 && studentFare == 0) return false;

        var fareEntity = new Fare
        {
            LineNumber = line.LineNumber,
            FullFare = fullFare,
            StudentFare = studentFare,
            DiscountedFare = discountedFare,
            RawJson = r.ToString()
        };

        if (isFirstLoad)
        {
            // Seed: save without notification
            await _repository.UpsertAsync(fareEntity);
            return false;
        }

        var storedFare = await _repository.GetByLineNumberAsync(line.LineNumber);

        if (storedFare == null || 
            storedFare.FullFare != fullFare || 
            storedFare.StudentFare != studentFare || 
            storedFare.DiscountedFare != discountedFare)
        {
            await NotifyFareChangeAsync(line, storedFare, fareEntity, fareData);
            await _repository.UpsertAsync(fareEntity);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts full, student, and discounted fares from the API response.
    /// </summary>
    private (decimal fullFare, decimal studentFare, decimal discountedFare) ExtractFares(JsonElement r)
    {
        decimal fullFare = 0, studentFare = 0, discountedFare = 0;

        if (r.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (group.TryGetProperty("routes", out var routes))
                {
                    foreach (var route in routes.EnumerateArray())
                    {
                        if (route.TryGetProperty("tariffs", out var tariffs))
                        {
                            foreach (var tariff in tariffs.EnumerateArray())
                            {
                                var finalFare = tariff.GetProperty("finalFare").GetDecimal();
                                var typeId = tariff.GetProperty("lineFareTypeId").GetInt32();
                                var typeName = GetTariffName(r, typeId);

                                if (typeName.Contains("Tam", StringComparison.OrdinalIgnoreCase)) fullFare = finalFare;
                                else if (typeName.Contains("Öğrenci", StringComparison.OrdinalIgnoreCase)) studentFare = finalFare;
                                else if (typeName.Contains("İndirimli", StringComparison.OrdinalIgnoreCase)) discountedFare = finalFare;
                            }
                        }
                    }
                }
            }
        }

        return (fullFare, studentFare, discountedFare);
    }

    private async Task<JsonElement?> FetchFareDataAsync(int lineApiId, CancellationToken token)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = string.Format(FareApiUrl, lineApiId);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
            request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await _retryPolicy.ExecuteAsync(async () => 
                await client.SendAsync(request, token));

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching fare data for line API id {LineApiId}", lineApiId);
            return null;
        }
    }

    /// <summary>
    /// Sends a Telegram notification about fare change for a specific line.
    /// </summary>
    private async Task NotifyFareChangeAsync(BusLine line, Fare? oldFare, Fare newFare, JsonElement? fareData)
    {
        var sb = new StringBuilder();
        var escapedLineNum = TelegramHelper.EscapeMarkdown(line.LineNumber);
        var escapedLineName = TelegramHelper.EscapeMarkdown(line.LineName);

        sb.AppendLine($"💰 *Ücret Değişikliği: {escapedLineNum} - {escapedLineName}*");
        sb.AppendLine();

        // Show old vs new comparison
        if (oldFare != null)
        {
            if (oldFare.FullFare != newFare.FullFare)
                sb.AppendLine($"🔸 Tam: ~{oldFare.FullFare:F2} TL~ → *{newFare.FullFare:F2} TL*");
            else
                sb.AppendLine($"🔹 Tam: {newFare.FullFare:F2} TL");

            if (oldFare.StudentFare != newFare.StudentFare)
                sb.AppendLine($"🔸 Öğrenci: ~{oldFare.StudentFare:F2} TL~ → *{newFare.StudentFare:F2} TL*");
            else if (newFare.StudentFare > 0)
                sb.AppendLine($"🔹 Öğrenci: {newFare.StudentFare:F2} TL");

            if (oldFare.DiscountedFare != newFare.DiscountedFare)
                sb.AppendLine($"🔸 İndirimli: ~{oldFare.DiscountedFare:F2} TL~ → *{newFare.DiscountedFare:F2} TL*");
            else if (newFare.DiscountedFare > 0)
                sb.AppendLine($"🔹 İndirimli: {newFare.DiscountedFare:F2} TL");
        }
        else
        {
            // First time seeing this line's fare (after seed)
            sb.AppendLine($"🔹 Tam: *{newFare.FullFare:F2} TL*");
            if (newFare.StudentFare > 0) sb.AppendLine($"🔹 Öğrenci: *{newFare.StudentFare:F2} TL*");
            if (newFare.DiscountedFare > 0) sb.AppendLine($"🔹 İndirimli: *{newFare.DiscountedFare:F2} TL*");
        }

        await _telegramHelper.SendMessageAsync(sb.ToString());

        try
        {
            var embed = DiscordEmbedBuilder.FareChanged(
                line.LineNumber, line.LineName,
                oldFare?.FullFare ?? 0, newFare.FullFare,
                oldFare?.StudentFare ?? 0, newFare.StudentFare,
                oldFare?.DiscountedFare ?? 0, newFare.DiscountedFare);
            
            var embedUrl = "https://ulasim.sakarya.bel.tr/ulasim/ucret-tarifeleri";
            await _discordHelper.SendEmbedWithButtonAsync("SAKUS", "sakarya-ulasim", embed, "💰 Ücret Tarifeleri", embedUrl);
        }
        catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
    }

    private string GetTariffName(JsonElement root, int typeId)
    {
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
