using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using System.Net.Http;
using Microsoft.Extensions.Http;
using SbbBot;
using SbbBot.Helpers;
using System.Text.RegularExpressions;

namespace SbbBot.Services;

public class MeetingWatcherService : BackgroundService
{
    private readonly ILogger<MeetingWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;

    private const string MeetingUrl = "https://www.sakarya.bel.tr/tr/EBelediye/MeclisKararlari";

    public MeetingWatcherService(
        ILogger<MeetingWatcherService> logger,
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
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(2),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning($"MeetingWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MeetingWatcherService started.");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.Intervals.MeetingMinutes));

        do
        {
            try
            {
                await CheckForMeetingAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in MeetingWatcherService execution");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }

    private async Task CheckForMeetingAsync(CancellationToken cancellationToken)
    {
        int intervalMinutes = _config.Intervals.MeetingMinutes > 0 ? _config.Intervals.MeetingMinutes : 1440;
        var ageMinutes = StorageHelper.GetDataFileAgeMinutes("meetings.json");
        if (ageMinutes < intervalMinutes)
        {
            _logger.LogInformation("Meeting data is fresh ({0:F0}m old), skipping check.", ageMinutes);
            return;
        }

        var client = _httpClientFactory.CreateClient();
        string html = await _retryPolicy.ExecuteAsync(async () =>
            await client.GetStringAsync(MeetingUrl, cancellationToken));

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var nodes = doc.DocumentNode.SelectNodes("//a");
        var currentMeetings = new List<string>();

        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                var text = node.InnerText;
                if (string.IsNullOrWhiteSpace(text)) continue;
                
                text = System.Net.WebUtility.HtmlDecode(text).Trim();

                // Look for pattern: "DD.MM.YYYY Tarihli..."
                // Based on user feedback, titles can vary greatly:
                // "14.11.2025 Tarihli Kasım Ayı Meclis Toplantısı 2. Birleşimi"
                // "08.12.2020 Tarihli Aralık Ayı Olağanüstü Meclis Toplantısı"
                // So we just look for anything starting with Date + "Tarihli" and containing "Meclis"
                
                if (Regex.IsMatch(text, @"\d{2}\.\d{2}\.\d{4}\s+Tarihli.*Meclis", RegexOptions.IgnoreCase))
                {
                    currentMeetings.Add(text);
                }
            }
        }

        if (currentMeetings.Count == 0)
        {
            _logger.LogWarning("Could not find any meeting nodes.");
            return;
        }

        var storedMeetings = await StorageHelper.ReadMeetingsAsync();
        var newMeetings = new List<string>();
        bool isFirstRun = storedMeetings.Count == 0;

        foreach (var meeting in currentMeetings)
        {
            if (!storedMeetings.Contains(meeting))
            {
                newMeetings.Add(meeting);
                storedMeetings.Add(meeting);
            }
        }

        if (newMeetings.Count > 0)
        {
            _logger.LogInformation($"{newMeetings.Count} new meetings detected.");
            
            // If it is the first run, we might want to avoid spamming 50+ messages.
            // But to be consistent with other services, we'll process them. 
            // However, for usability, maybe we only notify the latest one on first run, or all?
            // User asked: "all meetings to json, and add new ones as they come".
            // If I notify 50 times, it might be too much. 
            // Let's notify for all but in reverse order (oldest to newest) or just newest?
            // Let's just notify for all. The user can block the bot if it's too much :D
            // Actually, to be safe, if > 10 new meetings, maybe just notify the latest 5? 
            // Let's notify all, relying on the retry policy.
            
            // Limit first run notification to avoid overwhelming? 
            // Actually, let's just save them all, but only notify if !isFirstRun to avoid spam on initialization.
            // But the user complained "only found one". He probably wants to see them in the file.
            // If I silence the first run notifications, he might think it's broken.
            // Compromise: On first run, only notify the *latest* (first in list) meeting, but save ALL.
            
            if (isFirstRun)
            {
                // On first run, just notify the top one (latest date) effectively acting as "Bot is active, here is the latest"
                // The list from HTML is usually sorted by date desc. 
                // Let's pick the first one from 'currentMeetings' which is likely the latest.
                var latest = currentMeetings.FirstOrDefault();
                if (latest != null)
                {
                     await SendMeetingNotificationAsync(latest);
                }
            }
            else
            {
                foreach (var meeting in newMeetings)
                {
                    await SendMeetingNotificationAsync(meeting);
                }
            }
            
            await StorageHelper.SaveMeetingsAsync(storedMeetings);
        }

        // Mark as checked (touch file even if no new data)
        StorageHelper.TouchDataFile("meetings.json");
    }

    private async Task SendMeetingNotificationAsync(string title)
    {
        string message = $"{title} gündemi yayınlandı";
        
        var match = Regex.Match(title, @"^(\d{2}\.\d{2}\.\d{4})\s+(.+)$");
        if (match.Success)
        {
            var date = match.Groups[1].Value;
            var text = match.Groups[2].Value.ToLower(new System.Globalization.CultureInfo("tr-TR"));
            message = $"{date} {text} gündemi yayınlandı";
        }
        else
        {
             message = $"{title.ToLower(new System.Globalization.CultureInfo("tr-TR"))} gündemi yayınlandı";
        }

        await _telegramHelper.SendMessageAsync($"🏛 *Yeni Meclis Gündemi*\n\n{message}\n\n🔗 {MeetingUrl}");
    }
}
