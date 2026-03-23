using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SbbBot.Helpers;
using SbbBot.Models;
using SbbBot.Repositories;

namespace SbbBot.Services;

/// <summary>
/// Fetches ALL active line announcements from the public API in a SINGLE request.
/// This avoids per-line polling and is friendly to the server.
/// </summary>
public class AnnouncementWatcherService : BackgroundService
{
    private const string ApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/announcement?pageSize=100";

    private readonly ILogger<AnnouncementWatcherService> _logger;
    private readonly TelegramHelper _telegramHelper;
    private readonly IDiscordHelper _discordHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _pollInterval;
    private readonly AnnouncementRepository _repository;

    public AnnouncementWatcherService(
        ILogger<AnnouncementWatcherService> logger,
        TelegramHelper telegramHelper,
        IDiscordHelper discordHelper,
        IHttpClientFactory httpClientFactory,
        IOptions<BotConfig> config,
        AnnouncementRepository repository)
    {
        _logger = logger;
        _telegramHelper = telegramHelper;
        _discordHelper = discordHelper;
        _httpClientFactory = httpClientFactory;
        _repository = repository;
        _pollInterval = TimeSpan.FromMinutes(config.Value.Intervals.AnnouncementMinutes > 0
            ? config.Value.Intervals.AnnouncementMinutes
            : 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AnnouncementWatcherService started. Poll interval: {Interval}", _pollInterval);

        // Small startup delay so other services initialize first
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAnnouncementsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in AnnouncementWatcherService loop");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task CheckAnnouncementsAsync()
    {
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
        request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
        request.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reach announcement API");
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Announcement API returned {Status}", response.StatusCode);
            return;
        }

        var json = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array) return;

        var turkey = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");

        bool isFirstLoad = !await _repository.IsSeededAsync();

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetInt32();

            var title    = item.TryGetProperty("title",    out var tEl) ? tEl.GetString() ?? "" : "";
            var content  = item.TryGetProperty("content",  out var cEl) ? cEl.GetString() ?? "" : "";
            var lineName = item.TryGetProperty("lineName", out var lEl) ? lEl.GetString() ?? "" : "";
            var lineNum  = item.TryGetProperty("lineNumber", out var nEl) ? nEl.GetString() ?? "" : "";
            var category = item.TryGetProperty("categoryName", out var catEl) ? catEl.GetString() ?? "" : "";
            var slug     = item.TryGetProperty("slug",     out var sEl) ? sEl.GetString() ?? "" : "";
            var startDateStr = item.TryGetProperty("startDate", out var sdEl) ? sdEl.GetString() : null;
            var endDateStr   = item.TryGetProperty("endDate",   out var edEl) ? edEl.GetString() : null;

            DateTime? startDate = null;
            if (DateTime.TryParse(startDateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var start))
            {
                startDate = start.ToUniversalTime();
            }

            DateTime? endDate = null;
            if (DateTime.TryParse(endDateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var end))
            {
                endDate = end.ToUniversalTime();
            }

            var hash = ComputeHash($"{id}|{title}|{content}");

            bool exists = await _repository.ExistsAsync(id.ToString());

            var announcement = new Announcement
            {
                AnnouncementId = id.ToString(),
                Title = title,
                Content = content,
                StartDate = startDate,
                EndDate = endDate,
                ContentHash = hash,
                RawJson = item.ToString(),
                CreatedAt = DateTime.UtcNow
            };

            await _repository.InsertAsync(announcement);

            if (isFirstLoad || exists) continue;

            // Send notification
            var lineUrl = !string.IsNullOrEmpty(slug)
                ? $"https://ulasim.sakarya.bel.tr/ulasim/{slug}"
                : "https://ulasim.sakarya.bel.tr";

            var messageText = HtmlToMarkdown(content);

            var msg = new StringBuilder();
            msg.AppendLine($"📢 *Hat Duyurusu: {TelegramHelper.EscapeMarkdown(lineNum)} {TelegramHelper.EscapeMarkdown(lineName)}*");
            if (!string.IsNullOrEmpty(category))
                msg.AppendLine($"🏷 _{TelegramHelper.EscapeMarkdown(category)}_");
            msg.AppendLine();
            msg.AppendLine($"🔔 *{TelegramHelper.EscapeMarkdown(title)}*");
            msg.AppendLine();
            msg.AppendLine(messageText);

            if (startDate.HasValue)
            {
                var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startDate.Value, turkey);
                msg.Append($"\n📅 _{startLocal:dd.MM.yyyy HH:mm}");
                if (endDate.HasValue)
                {
                    var endLocal = TimeZoneInfo.ConvertTimeFromUtc(endDate.Value, turkey);
                    msg.Append($" — {endLocal:dd.MM.yyyy HH:mm}");
                }
                msg.AppendLine("_");
            }

            msg.AppendLine();
            msg.Append($"🔗 [Detaylar]({lineUrl})");

            await _telegramHelper.SendMessageAsync(msg.ToString());

            try
            {
                var discordContent = HtmlToDiscordMarkdown(content);
                var embed = DiscordEmbedBuilder.AnnouncementNew(title, discordContent, startDate ?? DateTime.UtcNow, endDate ?? DateTime.UtcNow);
                var buttonUrl = "https://ulasim.sakarya.bel.tr/duyurular";
                await _discordHelper.SendEmbedWithButtonAsync("SAKUS", "sakarya-ulasim", embed, "📢 Tüm Duyurular", buttonUrl);
            }
            catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }

            _logger.LogInformation("Announcement notification sent: [{Id}] {Title} ({Line})", id, title, lineName);
        }

        if (isFirstLoad)
        {
            await _repository.MarkAsSeededAsync();
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı, bildirim atlanıyor.", nameof(AnnouncementWatcherService));
        }
    }

    private static string ComputeHash(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// HTML içeriğini Discord markdown formatına dönüştürür.
    /// Discord: bold = **, italic = *
    /// </summary>
    private static string HtmlToDiscordMarkdown(string html)
    {
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<strong>(.*?)</strong>", "**$1**",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<em>(.*?)</em>", "*$1*",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<p>(.*?)</p>", "$1\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text;
    }

    private static string HtmlToMarkdown(string html)
    {
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<strong>(.*?)</strong>", "*$1*",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<em>(.*?)</em>", "_$1_",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<p>(.*?)</p>", "$1\n",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text;
    }
}
