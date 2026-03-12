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

public class UkomeWatcherService : BackgroundService
{
    private readonly ILogger<UkomeWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly IDiscordHelper _discordHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly HashRepository _repository;

    private const string MainUrl = "https://sakarya.bel.tr/tr/Anasayfa/UkomeKararlari";
    private const string BaseUrl = "https://sakarya.bel.tr";

    public UkomeWatcherService(
        ILogger<UkomeWatcherService> logger,
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
                    _logger.LogWarning($"UkomeWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UkomeWatcherService started.");

        int intervalMinutes = _config.Intervals.UkomeMinutes > 0 ? _config.Intervals.UkomeMinutes : 1440;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        do
        {
            try
            {
                await CheckForUkomeDecisionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in UkomeWatcherService execution");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }

    private async Task CheckForUkomeDecisionsAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        
        string mainHtml = await _retryPolicy.ExecuteAsync(async () =>
            await client.GetStringAsync(MainUrl, cancellationToken));

        var mainDoc = new HtmlDocument();
        mainDoc.LoadHtml(mainHtml);

        var yearNodes = mainDoc.DocumentNode.SelectNodes("//a");
        var validYearNodes = new List<HtmlNode>();

        if (yearNodes != null)
        {
            foreach (var node in yearNodes)
            {
                var href = node.GetAttributeValue("href", "");
                var text = node.InnerText;
                var alt = node.SelectSingleNode(".//img")?.GetAttributeValue("alt", "");
                
                bool isUkomeLink = (!string.IsNullOrWhiteSpace(href) && href.Contains("UKOME-Kararlari-")) ||
                                   (!string.IsNullOrWhiteSpace(text) && text.Contains("Yılı UKOME Kararları")) ||
                                   (!string.IsNullOrWhiteSpace(alt) && alt.Contains("Yılı UKOME Kararları"));

                if (isUkomeLink)
                {
                    validYearNodes.Add(node);
                }
            }
        }
        
        if (validYearNodes.Count == 0)
        {
            _logger.LogWarning("Could not find any Year links on UKOME main page.");
            return;
        }

        bool isFirstLoad = !await _repository.IsSeededAsync("ukome_decisions");
        int newDecisionsCount = 0;
        var newDecisionsDetailed = new List<(string Url, string Title)>();

        foreach (var yearNode in validYearNodes)
        {
            string yearUrl = yearNode.GetAttributeValue("href", "");
            string yearTitle = yearNode.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(yearTitle)) yearTitle = yearNode.SelectSingleNode(".//img")?.GetAttributeValue("alt", "") ?? "UKOME";
            
            yearTitle = Regex.Replace(yearTitle, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(yearUrl)) continue;

            if (!yearUrl.StartsWith("http"))
            {
                if (yearUrl.StartsWith("/")) yearUrl = BaseUrl + yearUrl;
                else yearUrl = BaseUrl + "/" + yearUrl;
            }

            try 
            {
                string yearHtml = await _retryPolicy.ExecuteAsync(async () =>
                    await client.GetStringAsync(yearUrl, cancellationToken));

                var doc = new HtmlDocument();
                doc.LoadHtml(yearHtml);

                var pdfNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '.pdf')]");

                if (pdfNodes != null)
                {
                    foreach (var node in pdfNodes)
                    {
                        string href = node.GetAttributeValue("href", "");
                        string text = node.InnerText.Trim();

                        if (string.IsNullOrWhiteSpace(href)) continue;
                        
                        if (!href.StartsWith("http"))
                        {
                           if (href.StartsWith("/")) href = BaseUrl + href;
                           else href = BaseUrl + "/" + href;
                        }

                        var hash = ComputeHash(href);
                        bool exists = await _repository.ExistsAsync("ukome_decisions", hash);

                        if (!exists)
                        {
                            await _repository.InsertAsync("ukome_decisions", hash, $"{text} ({yearTitle})", href, DateTime.UtcNow);
                            newDecisionsCount++;
                            newDecisionsDetailed.Add((href, $"{text} ({yearTitle})"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to scrape year page: {yearUrl}");
            }
        }

        if (!isFirstLoad)
        {
            if (newDecisionsCount > 0)
            {
                if (newDecisionsCount > 3)
                {
                     await _telegramHelper.SendMessageAsync($"🚦 *UKOME Kararları Güncellendi*\n\nToplam {newDecisionsCount} adet yeni karar eklendi.\nDetaylar için web sitesini ziyaret edebilirsiniz.");

                     foreach (var item in newDecisionsDetailed)
                     {
                         try
                         {
                             var embed = DiscordEmbedBuilder.UkomeDecision(item.Title, item.Url, DateTime.UtcNow);
                             await _discordHelper.SendEmbedWithButtonAsync("BasinDairesi", "ukome-kararlari", embed, "📋 Karara Git", item.Url);
                         }
                         catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
                     }
                }
                else
                {
                    foreach (var item in newDecisionsDetailed)
                    {
                        string info = string.IsNullOrWhiteSpace(item.Title) ? "Yeni Karar" : TelegramHelper.EscapeMarkdown(item.Title);
                        await _telegramHelper.SendMessageAsync($"🚦 *Yeni UKOME Kararı Eklendi*\n\n📄 {info}\n🔗 [PDF İndir]({item.Url})");

                        try
                        {
                            var embed = DiscordEmbedBuilder.UkomeDecision(item.Title, item.Url, DateTime.UtcNow);
                            await _discordHelper.SendEmbedWithButtonAsync("BasinDairesi", "ukome-kararlari", embed, "📋 Karara Git", item.Url);
                        }
                        catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
                    }
                }
            }
        }
        else
        {
            await _repository.MarkAsSeededAsync("ukome_decisions");
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı, bildirim atlanıyor.", nameof(UkomeWatcherService));
        }
    }

    private static string ComputeHash(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes);
    }
}
