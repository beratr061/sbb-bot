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

public class NewsWatcherService : BackgroundService
{
    private readonly ILogger<NewsWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;
    // Updated News URL as ulasim.sakarya.bel.tr/duyurular is 404
    private const string NewsUrl = "https://sakarya.bel.tr/tr/Haberler/1";

    public NewsWatcherService(
        ILogger<NewsWatcherService> logger,
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
                    _logger.LogWarning($"NewsWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NewsWatcherService started.");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.Intervals.NewsMinutes));

        do
        {
            try
            {
                await CheckForNewsAsync(stoppingToken); // Correct method name
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in NewsWatcherService execution");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }

    private async Task CheckForNewsAsync(CancellationToken cancellationToken)
    {
        int intervalMinutes = _config.Intervals.NewsMinutes > 0 ? _config.Intervals.NewsMinutes : 60;
        var ageMinutes = StorageHelper.GetDataFileAgeMinutes("news.json");
        if (ageMinutes < intervalMinutes)
        {
            _logger.LogInformation("News data is fresh ({0:F0}m old), skipping check.", ageMinutes);
            return;
        }

        var newsList = await FetchLatestNewsAsync(cancellationToken);
        if (newsList == null || newsList.Count == 0) return;

        var storedNews = await StorageHelper.ReadNewsHistoryAsync();
        var newNewsItems = new List<string>();
        bool isFirstRun = storedNews.Count == 0;

        foreach (var newsItem in newsList)
        {
            // Format: TITLE|LINK
            var parts = newsItem.Split('|');
            if (parts.Length < 2) continue;
            
            var link = parts[1];

            if (!storedNews.Contains(link))
            {
                newNewsItems.Add(newsItem);
                storedNews.Add(link);
            }
        }

        if (newNewsItems.Count > 0)
        {
             await StorageHelper.SaveNewsHistoryAsync(storedNews);

             if (!isFirstRun)
             {
                 foreach (var item in newNewsItems)
                 {
                     var parts = item.Split('|');
                     var title = parts[0];
                     var link = parts[1];
                     
                     _logger.LogInformation($"New transport news detected: {title}");
                     await _telegramHelper.SendMessageAsync($"📢 *Yeni Ulaşım Haberi*\n\n📰 {title}\n🔗 [Haberi Oku]({link})");
                 }
             }
             else
             {
                 _logger.LogInformation($"Initial news fetch complete. found {newNewsItems.Count} relevant news items.");
             }
        }

        // Mark as checked (touch file even if no new data)
        StorageHelper.TouchDataFile("news.json");
    }

    private async Task<List<string>> FetchLatestNewsAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        
        string html = await _retryPolicy.ExecuteAsync(async () => 
            await client.GetStringAsync(NewsUrl, cancellationToken));

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // More robust XPath: Select all anchor tags
        var allLinks = doc.DocumentNode.SelectNodes("//a");

        if (allLinks == null)
        {
            _logger.LogWarning("Could not find ANY links on Sakarya BB News page.");
            return new List<string>();
        }

        var newsList = new List<string>();

        // Keywords to filter for Transportation related news
        var transportKeywords = new[] 
        { 
            "ulaşım", "otobüs", "sefer", "güzergah", "yolcu", "kart54", "durak", 
            "trafik", "kavşak", "yol yapım", "asfalt", "ukome", "minibüs", "taksi", 
            "servis", "garaj", "terminal", "metropol", "raylı", "sakus", "metrobüs" 
        };

        foreach (var node in allLinks)
        {
            var href = node.GetAttributeValue("href", "");
            
            // Check if it looks like a news link
            if (string.IsNullOrWhiteSpace(href)) continue;
            
            // Expected format: /tr/Haber/slug/id or similar
            if (!href.Contains("/Haber/", StringComparison.OrdinalIgnoreCase)) continue;

            var title = node.InnerText.Trim();
            
            // Fix HTML encoding (e.g. &#252; -> ü)
            title = System.Net.WebUtility.HtmlDecode(title);

            // Clean up title
            title = Regex.Replace(title, @"\s+", " ").Trim();
            
            // Fallback to img alt if text is empty
            if (title.Length < 5)
            {
                var img = node.SelectSingleNode(".//img");
                if (img != null)
                {
                    title = img.GetAttributeValue("alt", "").Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(title) || title.Length < 5) continue;

            // Ensure absolute URL
            if (!href.StartsWith("http"))
            {
                href = "https://sakarya.bel.tr" + (href.StartsWith("/") ? "" : "/") + href;
            }

            // FILTER: Check if Title contains any transport keyword
            bool isRelevant = transportKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (isRelevant)
            {
                // Unique Key: LINK
                // We'll store Title|Link
                newsList.Add($"{title}|{href}");
            }
        }

        return newsList.Distinct().ToList();
    }
}
