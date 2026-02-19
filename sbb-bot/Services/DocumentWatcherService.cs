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

namespace SbbBot.Services;

public class DocumentWatcherService : BackgroundService
{
    private readonly ILogger<DocumentWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;
    private const string DocumentsUrl = "https://www.sakarya.bel.tr/tr/StratejikPlanlama";

    public DocumentWatcherService(
        ILogger<DocumentWatcherService> logger,
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
                    _logger.LogWarning($"DocumentWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DocumentWatcherService started.");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.Intervals.DocumentsMinutes));

        do
        {
            try
            {
                await CheckForDocumentsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in DocumentWatcherService execution");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }

    private async Task CheckForDocumentsAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();

        string html = await _retryPolicy.ExecuteAsync(async () =>
            await client.GetStringAsync(DocumentsUrl, cancellationToken));

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Find all links ending with .pdf
        var pdfNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '.pdf')]");
        
        if (pdfNodes == null || pdfNodes.Count == 0)
        {
            _logger.LogInformation("No PDF documents found.");
            return;
        }

        var currentDocuments = new HashSet<string>();
        var newDocuments = new List<(string Title, string Url)>();
        var oldDocuments = await StorageHelper.ReadDocumentsAsync();

        foreach (var node in pdfNodes)
        {
            string title = node.InnerText.Trim();
            string href = node.GetAttributeValue("href", string.Empty);

            if (string.IsNullOrWhiteSpace(href)) continue;

            if (!href.StartsWith("http"))
            {
                var uri = new Uri(DocumentsUrl);
                href = new Uri(uri, href).ToString();
            }
            
            // Use URL as unique key
            currentDocuments.Add(href);

            if (!oldDocuments.Contains(href))
            {
                newDocuments.Add((title, href));
            }
        }

        if (newDocuments.Any())
        {
            _logger.LogInformation($"{newDocuments.Count} new documents detected.");
            foreach (var docItem in newDocuments)
            {
                var message = $"📄 *Yeni Resmi Belge Eklendi!*\n\nDosya: {docItem.Title}\nLink: [İndir]({docItem.Url})";
                await _telegramHelper.SendMessageAsync(message);
            }
            
            // Update storage with ALL current documents found
            // effectively initializing if it was empty, or updating if new ones added.
            // We usually want to keep the union or just the current state.
            // Requirement says "Yeni eklenen dosyalar".
            // If we just save currentDocuments, we are good.
            await StorageHelper.SaveDocumentsAsync(currentDocuments);
        }
        else if (currentDocuments.Count != oldDocuments.Count)
        {
             // If counts differ but no new documents, maybe some were removed.
             // We update the list to reflect current state so we don't track deleted files forever.
             await StorageHelper.SaveDocumentsAsync(currentDocuments);
        }
    }
}
