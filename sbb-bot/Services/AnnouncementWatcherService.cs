using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SbbBot.Helpers;

namespace SbbBot.Services;

/// <summary>
/// Fetches ALL active line announcements from the public API in a SINGLE request.
/// This avoids per-line polling and is friendly to the server.
///
/// API: GET https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/announcement?pageSize=100
/// Returns all currently active announcements across all lines.
/// </summary>
public class AnnouncementWatcherService : BackgroundService
{
    private const string ApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/announcement?pageSize=100";
    private const string StateFile = "Data/announcements.json";

    private readonly ILogger<AnnouncementWatcherService> _logger;
    private readonly TelegramHelper _telegramHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _pollInterval;

    // In-memory state: announcementId -> hash of (title + content)
    // Persisted to disk so restarts don't cause duplicate notifications.
    private Dictionary<int, string> _seenHashes = new();

    public AnnouncementWatcherService(
        ILogger<AnnouncementWatcherService> logger,
        TelegramHelper telegramHelper,
        IHttpClientFactory httpClientFactory,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _telegramHelper = telegramHelper;
        _httpClientFactory = httpClientFactory;
        _pollInterval = TimeSpan.FromMinutes(config.Value.Intervals.AnnouncementMinutes > 0
            ? config.Value.Intervals.AnnouncementMinutes
            : 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AnnouncementWatcherService started. Poll interval: {Interval}", _pollInterval);

        LoadState();

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

        var activeIds = new HashSet<int>();
        var stateChanged = false;
        var turkey = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetInt32();
            activeIds.Add(id);

            var title    = item.TryGetProperty("title",    out var tEl) ? tEl.GetString() ?? "" : "";
            var content  = item.TryGetProperty("content",  out var cEl) ? cEl.GetString() ?? "" : "";
            var lineName = item.TryGetProperty("lineName", out var lEl) ? lEl.GetString() ?? "" : "";
            var lineNum  = item.TryGetProperty("lineNumber", out var nEl) ? nEl.GetString() ?? "" : "";
            var category = item.TryGetProperty("categoryName", out var catEl) ? catEl.GetString() ?? "" : "";
            var slug     = item.TryGetProperty("slug",     out var sEl) ? sEl.GetString() ?? "" : "";
            var startDate = item.TryGetProperty("startDate", out var sdEl) ? sdEl.GetString() : null;
            var endDate   = item.TryGetProperty("endDate",   out var edEl) ? edEl.GetString() : null;

            // Hash detects real content changes
            var hash = ComputeHash($"{id}|{title}|{content}");

            if (_seenHashes.TryGetValue(id, out var existingHash) && existingHash == hash)
                continue; // No change for this announcement

            // New or updated announcement — send notification
            _seenHashes[id] = hash;
            stateChanged = true;

            var lineUrl = !string.IsNullOrEmpty(slug)
                ? $"https://ulasim.sakarya.bel.tr/ulasim/{slug}"
                : "https://ulasim.sakarya.bel.tr";

            var messageText = HtmlToMarkdown(content);

            var msg = new StringBuilder();
            msg.AppendLine($"📢 *Hat Duyurusu: {lineNum} {lineName}*".Trim('*').Trim() is var plain
                ? $"📢 *Hat Duyurusu: {lineNum} {lineName}*"
                : "");
            if (!string.IsNullOrEmpty(category))
                msg.AppendLine($"🏷 _{category}_");
            msg.AppendLine();
            msg.AppendLine($"🔔 *{title}*");
            msg.AppendLine();
            msg.AppendLine(messageText);

            if (!string.IsNullOrEmpty(startDate) && DateTime.TryParse(startDate, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var start))
            {
                var startLocal = TimeZoneInfo.ConvertTimeFromUtc(start, turkey);
                msg.Append($"\n📅 _{startLocal:dd.MM.yyyy HH:mm}");
                if (!string.IsNullOrEmpty(endDate) && DateTime.TryParse(endDate, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var end))
                {
                    var endLocal = TimeZoneInfo.ConvertTimeFromUtc(end, turkey);
                    msg.Append($" — {endLocal:dd.MM.yyyy HH:mm}");
                }
                msg.AppendLine("_");
            }

            msg.AppendLine();
            msg.Append($"🔗 [Detaylar]({lineUrl})");

            await _telegramHelper.SendMessageAsync(msg.ToString());
            _logger.LogInformation("Announcement notification sent: [{Id}] {Title} ({Line})", id, title, lineName);
        }

        // Remove IDs that are no longer in the active list
        // so they'll be notified again if they come back.
        var removedIds = _seenHashes.Keys.Except(activeIds).ToList();
        foreach (var rid in removedIds)
        {
            _seenHashes.Remove(rid);
            stateChanged = true;
            _logger.LogInformation("Announcement {Id} is no longer active, cleared from state", rid);
        }

        if (stateChanged) SaveState();

        // Save full announcement data for InteractionManager to read
        SaveAnnouncementData(json);
    }

    // ── State persistence ─────────────────────────────────────────────────────

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var json = File.ReadAllText(StateFile);
            _seenHashes = JsonSerializer.Deserialize<Dictionary<int, string>>(json) ?? new();
            _logger.LogInformation("Loaded {Count} announcement state entries", _seenHashes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load announcement state — starting fresh");
            _seenHashes = new();
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            var json = JsonSerializer.Serialize(_seenHashes,
                new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(StateFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save announcement state");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SaveAnnouncementData(string json)
    {
        try
        {
            var path = Path.Combine(StorageHelper.GetDataPath(), "announcement_data.json");
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save announcement data");
        }
    }

    private static string ComputeHash(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes);
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
