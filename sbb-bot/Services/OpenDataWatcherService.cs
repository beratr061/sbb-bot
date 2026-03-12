using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SbbBot.Helpers;
using SbbBot.Repositories;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SbbBot.Services;

public class OpenDataWatcherService : BackgroundService
{
    private const string ApiUrl = "https://veri.sakarya.bel.tr/api/3/action/group_activity_list?id=ulasim";

    private readonly ILogger<OpenDataWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly IDiscordHelper _discordHelper;
    private readonly HashRepository _repository;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(4);

    public OpenDataWatcherService(
        ILogger<OpenDataWatcherService> logger,
        IHttpClientFactory httpClientFactory,
        TelegramHelper telegramHelper,
        IDiscordHelper discordHelper,
        HashRepository repository)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _telegramHelper = telegramHelper;
        _discordHelper = discordHelper;
        _repository = repository;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OpenDataWatcherService started.");
        
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
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync(ApiUrl, token);
        if (!response.IsSuccessStatusCode) return;

        var json = await response.Content.ReadAsStringAsync(token);
        var result = JsonSerializer.Deserialize<OpenDataResponse>(json);

        if (result == null || result.Success == false || result.Result == null) return;

        bool isFirstLoad = !await _repository.IsSeededAsync("open_data_sets");
        
        // Sort ascending so oldest new activity is processed first
        var activities = result.Result.OrderBy(x => x.Timestamp).ToList();

        foreach (var act in activities)
        {
            string id = act.ObjectId ?? act.Id;
            string title = act.Data?.Package?.Title ?? "Bilinmeyen Veri Seti";

            bool exists = await _repository.ExistsAsync("open_data_sets", id);

            if (!exists)
            {
                await _repository.InsertAsync("open_data_sets", id, title, "", act.Timestamp);

                if (!isFirstLoad)
                {
                    await SendNotificationAsync(act);
                }
            }
        }

        if (isFirstLoad)
        {
            await _repository.MarkAsSeededAsync("open_data_sets");
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı, bildirim atlanıyor.", nameof(OpenDataWatcherService));
        }
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

        var datasetUrl = $"https://veri.sakarya.bel.tr/dataset/{activity.ObjectId}";

        var msg = $"{type}\n\n" +
                  $"📂 *{title}*\n" +
                  $"📅 {activity.Timestamp:dd.MM.yyyy HH:mm}\n\n" +
                  $"🔗 [Detaylar]({datasetUrl})";

        await _telegramHelper.SendMessageAsync(msg);

        try
        {
            bool isNew = activity.ActivityType == "new package";
            var embed = DiscordEmbedBuilder.OpenDataUpdated(title, datasetUrl, isNew);
            await _discordHelper.SendEmbedWithButtonAsync("BasinDairesi", "acik-veri-portali", embed, "📊 Veri Setine Git", datasetUrl);
        }
        catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
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
