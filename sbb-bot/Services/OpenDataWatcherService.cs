using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SbbBot.Helpers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SbbBot.Services;

public class OpenDataWatcherService : BackgroundService
{
    private const string ApiUrl = "https://veri.sakarya.bel.tr/api/3/action/group_activity_list?id=ulasim";
    private const string StateFile = "Data/opendata_state.txt";

    private readonly ILogger<OpenDataWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(4); // Check every 4 hours

    public OpenDataWatcherService(
        ILogger<OpenDataWatcherService> logger,
        IHttpClientFactory httpClientFactory,
        TelegramHelper telegramHelper)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _telegramHelper = telegramHelper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OpenDataWatcherService started.");
        
        // Initial delay
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        using var timer = new PeriodicTimer(_checkInterval);
        do
        {
            try
            {
                await CheckOpenDataAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OpenDataWatcherService");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }

    private async Task CheckOpenDataAsync(CancellationToken token)
    {
        var ageHours = StorageHelper.GetDataFileAgeHours("opendata_state.txt");
        if (ageHours < _checkInterval.TotalHours)
        {
            _logger.LogInformation("Open data is fresh ({0:F1}h old), skipping check.", ageHours);
            return;
        }

        var client = _httpClientFactory.CreateClient();
        // CKAN API typically returns JSON
        var response = await client.GetAsync(ApiUrl, token);
        if (!response.IsSuccessStatusCode) return;

        var json = await response.Content.ReadAsStringAsync(token);
        var result = JsonSerializer.Deserialize<OpenDataResponse>(json);

        if (result == null || result.Success == false || result.Result == null) return;

        // Get the most recent activity
        var latestActivity = result.Result.OrderByDescending(x => x.Timestamp).FirstOrDefault();
        if (latestActivity == null) return;

        var storedLastId = await StorageHelper.ReadStateAsync(StateFile);

        if (storedLastId != latestActivity.Id)
        {
            // Only notify if we have a previous state (to avoid spam on first run)
            // But actually for this low frequency, notifying on first run might be okay?
            // Let's assume on first run we just save state to establish baseline.
            
            if (!string.IsNullOrEmpty(storedLastId))
            {
                await SendNotificationAsync(latestActivity);
            }

            await StorageHelper.SaveStateAsync(StateFile, latestActivity.Id);
        }

        // Mark as checked (touch file even if no change)
        StorageHelper.TouchDataFile("opendata_state.txt");
    }

    private async Task SendNotificationAsync(OpenDataActivity activity)
    {
        var title = activity.Data?.Package?.Title ?? "Bilinmeyen Veri Seti";
        var type = activity.ActivityType switch
        {
            "new package" => "🆕 Yeni Verı Seti",
            "changed package" => "🔄 Veri Seti Güncellendi",
            "deleted package" => "🗑️ Veri Seti Silindi",
            _ => "📢 Veri Portalı Aktivitesi"
        };

        var msg = $"{type}\n\n" +
                  $"📂 *{title}*\n" +
                  $"📅 {activity.Timestamp:dd.MM.yyyy HH:mm}\n\n" +
                  $"🔗 [Detaylar](https://veri.sakarya.bel.tr/dataset/{activity.ObjectId})";

        await _telegramHelper.SendMessageAsync(msg);
    }
}

// Models
public class OpenDataResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("result")]
    public List<OpenDataActivity> Result { get; set; } = new();
}

public class OpenDataActivity
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("activity_type")]
    public string ActivityType { get; set; } = ""; // "new package", "changed package"

    [JsonPropertyName("object_id")]
    public string ObjectId { get; set; } = ""; // dataset slug/id

    [JsonPropertyName("data")]
    public ActivityData? Data { get; set; }
}

public class ActivityData
{
    [JsonPropertyName("package")]
    public ActivityPackage? Package { get; set; }
}

public class ActivityPackage
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
}
