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

public class UkomeWatcherService : BackgroundService
{
    private readonly ILogger<UkomeWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;

    private const string MainUrl = "https://sakarya.bel.tr/tr/Anasayfa/UkomeKararlari";
    private const string BaseUrl = "https://sakarya.bel.tr";

    public UkomeWatcherService(
        ILogger<UkomeWatcherService> logger,
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
                    _logger.LogWarning($"UkomeWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UkomeWatcherService started.");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.Intervals.UkomeMinutes));

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
        
        // 1. Fetch Main Page to find ALL Year links
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
                // Check if link points to a UKOME year page
                var href = node.GetAttributeValue("href", "");
                var text = node.InnerText; // Raw inner text
                var alt = node.SelectSingleNode(".//img")?.GetAttributeValue("alt", "");

                // Robust check strategies:
                // 1. URL pattern: /tr/Sayfa/UKOME-Kararlari-YYYY
                // 2. Text/Alt contains "Yılı UKOME Kararları"
                
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

        var storedDecisions = await StorageHelper.ReadUkomeDecisionsAsync();
        var storedYears = await StorageHelper.ReadUkomeYearsAsync();
        
        bool isFirstRun = storedDecisions.Count == 0;
        var newDecisions = new List<string>(); 
        var decisionDetails = new Dictionary<string, string>(); 
        var newYearTitles = new List<string>();

        // Loop through each year
        foreach (var yearNode in validYearNodes)
        {
            string yearUrl = yearNode.GetAttributeValue("href", "");
            string yearTitle = yearNode.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(yearTitle)) yearTitle = yearNode.SelectSingleNode(".//img")?.GetAttributeValue("alt", "") ?? "UKOME";
            
            // Cleanup Title (remove newlines usually present with img)
            yearTitle = Regex.Replace(yearTitle, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(yearUrl)) continue;

            if (!yearUrl.StartsWith("http"))
            {
                if (yearUrl.StartsWith("/")) yearUrl = BaseUrl + yearUrl;
                else yearUrl = BaseUrl + "/" + yearUrl;
            }

            // Check if this is a NEW year
            if (!storedYears.Contains(yearUrl))
            {
                if (!isFirstRun)
                {
                    // New Year detected! (e.g. 2026 coming out)
                    newYearTitles.Add(yearTitle);
                }
                storedYears.Add(yearUrl);
            }

            try 
            {
                // 2. Fetch the Year Page
                string yearHtml = await _retryPolicy.ExecuteAsync(async () =>
                    await client.GetStringAsync(yearUrl, cancellationToken));

                var doc = new HtmlDocument();
                doc.LoadHtml(yearHtml);

                // 3. Extract Decisions (PDF Links)
                var pdfNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '.pdf')]");

                if (pdfNodes != null)
                {
                    foreach (var node in pdfNodes)
                    {
                        string href = node.GetAttributeValue("href", "");
                        string text = node.InnerText.Trim();

                        if (string.IsNullOrWhiteSpace(href)) continue;
                        
                        // Ensure absolute URL (Unique Identifier)
                        if (!href.StartsWith("http"))
                        {
                           if (href.StartsWith("/")) href = BaseUrl + href;
                           else href = BaseUrl + "/" + href;
                        }

                        // Filter: Must look like a UKOME decision or reside on this page.
                        if (!storedDecisions.Contains(href))
                        {
                            newDecisions.Add(href);
                            storedDecisions.Add(href);
                            
                            if (!decisionDetails.ContainsKey(href))
                            {
                                decisionDetails[href] = $"{text} ({yearTitle})";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to scrape year page: {yearUrl}");
            }
        }

        // Save State
        if (newDecisions.Count > 0)
        {
            await StorageHelper.SaveUkomeDecisionsAsync(storedDecisions);
        }
        if (storedYears.Count > 0) // Should ideally optimize to save only if changed
        {
             await StorageHelper.SaveUkomeYearsAsync(storedYears);
        }

        // Notifications
        if (!isFirstRun)
        {
            // 1. Notify about New Years
            foreach (var title in newYearTitles)
            {
                 await _telegramHelper.SendMessageAsync($"📢 *Yeni UKOME Yılı Yayınlandı*\n\n📅 {title} dönemi kararları sisteme düşmeye başladı!");
            }

            // 2. Notify about New Decisions
            if (newDecisions.Count > 0)
            {
                // If many decisions (batch update), send summary
                if (newDecisions.Count > 3)
                {
                     // Group by Year for summary? Or simple summary.
                     // The user wants: "2026 Yılı UKOME Kararlarına Yeni Kararlar eklendi" 
                     // We can infer the year from the decisions or just say generic.
                     
                     // Let's optimize: If we have multiple decisions, we can send a digest.
                     await _telegramHelper.SendMessageAsync($"🚦 *UKOME Kararları Güncellendi*\n\nToplam {newDecisions.Count} adet yeni karar eklendi.\nDetaylar için web sitesini ziyaret edebilirsiniz.");
                }
                else
                {
                    // Few decisions -> Send details
                    foreach (var url in newDecisions)
                    {
                        string info = decisionDetails.ContainsKey(url) ? decisionDetails[url] : "Yeni Karar";
                        await _telegramHelper.SendMessageAsync($"🚦 *Yeni UKOME Kararı Eklendi*\n\n📄 {info}\n🔗 [PDF İndir]({url})");
                    }
                }
            }
        }
        else
        {
             // First Run Summary
             if (newDecisions.Count > 0)
             {
                 await _telegramHelper.SendMessageAsync($"🚦 *UKOME Kararları Arşivi Güncellendi*\n\nToplam {newDecisions.Count} adet karar veritabanına eklendi. Yeni yıllar ve kararlar eklendikçe bildirim gelecektir.");
             }
        }
    }
}
