using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using SbbBot.Helpers;
using SbbBot.Repositories;

namespace SbbBot.Services;

public class DocumentWatcherService : BackgroundService
{
    private readonly ILogger<DocumentWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly IDiscordHelper _discordHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly HashRepository _repository;
    
    private const string DocumentsUrl = "https://www.sakarya.bel.tr/tr/StratejikPlanlama";

    public DocumentWatcherService(
        ILogger<DocumentWatcherService> logger,
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
                    _logger.LogWarning($"DocumentWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DocumentWatcherService started.");

        int intervalMinutes = _config.Intervals.DocumentsMinutes > 0 ? _config.Intervals.DocumentsMinutes : 1440;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

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

        var pdfNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '.pdf')]");
        
        if (pdfNodes == null || pdfNodes.Count == 0)
        {
            _logger.LogInformation("No PDF documents found.");
            return;
        }

        bool isFirstLoad = !await _repository.IsSeededAsync("documents");

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
            
            var hash = ComputeHash(href);
            bool exists = await _repository.ExistsAsync("documents", hash);

            // Save to DB
            if (!exists)
            {
                await _repository.InsertAsync("documents", hash, string.IsNullOrEmpty(title) ? "Belge" : title, href, DateTime.UtcNow);
                
                if (!isFirstLoad)
                {
                    var message = $"📄 *Yeni Resmi Belge Eklendi!*\n\nDosya: {TelegramHelper.EscapeMarkdown(title)}\nLink: [İndir]({href})";
                    await _telegramHelper.SendMessageAsync(message);

                    try
                    {
                        var embed = DiscordEmbedBuilder.DocumentPublished(title, href, DateTime.UtcNow);
                        await _discordHelper.SendEmbedWithButtonAsync("BasinDairesi", "butce-ve-stratejik-yonetim", embed, "📄 Dökümana Git", href);
                    }
                    catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
                }
            }
        }

        if (isFirstLoad)
        {
            await _repository.MarkAsSeededAsync("documents");
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı, bildirim atlanıyor.", nameof(DocumentWatcherService));
        }
    }

    private static string ComputeHash(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes);
    }
}
