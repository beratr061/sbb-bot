using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using SbbBot.Helpers;
using SbbBot.Repositories;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SbbBot.Services;

public class NewsWatcherService : BackgroundService
{
    private readonly ILogger<NewsWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly IDiscordHelper _discordHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly HashRepository _repository;
    
    // Updated News URL as ulasim.sakarya.bel.tr/duyurular is 404
    private const string NewsUrl = "https://sakarya.bel.tr/tr/Haberler/1";

    public NewsWatcherService(
        ILogger<NewsWatcherService> logger,
        IHttpClientFactory httpClientFactory,
        TelegramHelper telegramHelper,
        IDiscordHelper discordHelper,
        IOptions<BotConfig> config,
        HashRepository repository)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _telegramHelper = telegramHelper;
        _discordHelper = discordHelper;
        _config = config.Value;
        _repository = repository;

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

        int intervalMinutes = _config.Intervals.NewsMinutes > 0 ? _config.Intervals.NewsMinutes : 60;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

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
        var newsList = await FetchLatestNewsAsync(cancellationToken);
        if (newsList == null || newsList.Count == 0) return;

        bool isFirstLoad = !await _repository.IsSeededAsync("news");

        foreach (var newsItem in newsList)
        {
            // Format: TITLE|LINK
            var parts = newsItem.Split('|');
            if (parts.Length < 2) continue;
            
            var title = parts[0];
            var link = parts[1];

            var hash = ComputeHash(link);
            bool exists = await _repository.ExistsAsync("news", hash);

            if (!exists)
            {
                await _repository.InsertAsync("news", hash, title, link, DateTime.UtcNow);

                if (!isFirstLoad)
                {
                    _logger.LogInformation($"New transport news detected: {title}");
                    await _telegramHelper.SendMessageAsync($"📢 *Yeni Ulaşım Haberi*\n\n📰 {TelegramHelper.EscapeMarkdown(title)}\n🔗 [Haberi Oku]({link})");

                    try
                    {
                        var embed = DiscordEmbedBuilder.NewsPublished(title, link, DateTime.UtcNow);
                        await _discordHelper.SendEmbedWithButtonAsync("BasinDairesi", "haberler", embed, "📰 Habere Git", link);
                    }
                    catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
                }
            }
        }

        if (isFirstLoad)
        {
            await _repository.MarkAsSeededAsync("news");
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı, bildirim atlanıyor.", nameof(NewsWatcherService));
        }
    }

    private static string ComputeHash(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes);
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
