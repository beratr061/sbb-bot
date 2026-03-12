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
    private readonly SbbBot.Repositories.HashRepository _repository;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;

    private const string MeetingUrl = "https://www.sakarya.bel.tr/tr/EBelediye/MeclisKararlari";

    public MeetingWatcherService(
        ILogger<MeetingWatcherService> logger,
        IHttpClientFactory httpClientFactory,
        TelegramHelper telegramHelper,
        SbbBot.Repositories.HashRepository repository,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _telegramHelper = telegramHelper;
        _repository = repository;
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

        int intervalMinutes = _config.Intervals.MeetingMinutes > 0 ? _config.Intervals.MeetingMinutes : 1440;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

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

        var isFirstLoad = !await _repository.IsSeededAsync("meetings");

        foreach (var meeting in currentMeetings)
        {
            var hash = ComputeHash(meeting);
            if (!await _repository.ExistsAsync("meetings", hash))
            {
                await _repository.InsertAsync("meetings", hash, meeting, MeetingUrl, DateTime.UtcNow);

                if (!isFirstLoad)
                {
                    await SendMeetingNotificationAsync(meeting);
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        if (isFirstLoad)
        {
            await _repository.MarkAsSeededAsync("meetings");
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı, bildirim atlanıyor.", nameof(MeetingWatcherService));
        }
    }

    private string ComputeHash(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToBase64String(bytes);
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
