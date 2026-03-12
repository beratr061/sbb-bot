using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using SbbBot.Helpers;
using SbbBot.Models;
using System.Text.Json;

namespace SbbBot.Services;

public class RouteWatcherService : BackgroundService
{
    private readonly ILogger<RouteWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly IDiscordHelper _discordHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly SbbBot.Repositories.RouteRepository _repository;
    private readonly IDbConnectionFactory _dbFactory;

    private const string ListApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim?busType={0}";
    private const string RouteApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/route-and-busstops/{0}?date={1}";
    
    // We monitor both types
    private const int BusTypeBelediye = 3869;
    private const int BusTypeOzelHalk = 5731;
    private const int BusTypeTaksiDolmus = 5733;
    private const int BusTypeMinibus = 5732;

    public RouteWatcherService(
        ILogger<RouteWatcherService> logger,
        IHttpClientFactory httpClientFactory,
        TelegramHelper telegramHelper,
        IDiscordHelper discordHelper,
        IOptions<BotConfig> config,
        SbbBot.Repositories.RouteRepository repository,
        IDbConnectionFactory dbFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _telegramHelper = telegramHelper;
        _discordHelper = discordHelper;
        _config = config.Value;
        _repository = repository;
        _dbFactory = dbFactory;

        _retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(2),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning($"RouteWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RouteWatcherService started.");

        int intervalMinutes = _config.Intervals.RouteMinutes > 0 ? _config.Intervals.RouteMinutes : 1440;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        do
        {
            try
            {
                await CheckRoutesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RouteWatcherService execution");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }

    private async Task CheckRoutesAsync(CancellationToken cancellationToken)
    {
        var allLines = new HashSet<int>();
        var belediyeIds = await FetchLineIdsAsync(BusTypeBelediye, cancellationToken);
        var ozelIds = await FetchLineIdsAsync(BusTypeOzelHalk, cancellationToken);
        var taksiIds = await FetchLineIdsAsync(BusTypeTaksiDolmus, cancellationToken);
        var minibusIds = await FetchLineIdsAsync(BusTypeMinibus, cancellationToken);
        
        foreach (var id in belediyeIds) allLines.Add(id);
        foreach (var id in ozelIds) allLines.Add(id);
        foreach (var id in taksiIds) allLines.Add(id);
        foreach (var id in minibusIds) allLines.Add(id);

        _logger.LogInformation($"Checking routes for {allLines.Count} lines...");
        bool isFirstLoad = !await _repository.IsSeededAsync();

        foreach (var lineId in allLines)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await ProcessLineRouteAsync(lineId, isFirstLoad, cancellationToken);
            
            await Task.Delay(1000, cancellationToken);
        }

        if (isFirstLoad)
        {
            await _repository.MarkAsSeededAsync();
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı, bildirim atlanıyor.", nameof(RouteWatcherService));
        }
    }

    private async Task ProcessLineRouteAsync(int lineId, bool isFirstLoad, CancellationToken token)
    {
        var currentRoute = await FetchRouteDetailsAsync(lineId, token);
        if (currentRoute == null) return;

        currentRoute.LastChecked = DateTime.UtcNow;

        RouteResponse? storedRoute = null;
        var rawJson = await _repository.GetRawJsonAsync(lineId.ToString(), "ALL");
        if (!string.IsNullOrEmpty(rawJson))
        {
            storedRoute = JsonSerializer.Deserialize<RouteResponse>(rawJson);
        }

        var newJson = JsonSerializer.Serialize(currentRoute);

        if (storedRoute == null)
        {
            // Compute a simple hash based on JSON content
            var newHash = System.Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(newJson)));
            await _repository.UpsertAsync(lineId.ToString(), "ALL", newHash, newJson);
            return;
        }

        var changes = CompareRoutes(storedRoute, currentRoute);
        if (changes.Count > 0)
        {
            if (!isFirstLoad)
            {
                await NotifyChangesAsync(currentRoute, changes);
            }
            // Update even if no changes visually but LastChecked might be updated...
            // Wait, we only want to update if there are changes.
        }

        // We update the DB if there are changes, or just to keep LastChecked updated?
        // Let's always update DB so rawJson matches current state and LastChecked.
        var finalHash = System.Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(newJson)));
        await _repository.UpsertAsync(lineId.ToString(), "ALL", finalHash, newJson);
        await SaveBusStopsAsync(currentRoute);
    }

    /// <summary>
    /// Saves bus stop data from a RouteResponse into the bus_stops table.
    /// </summary>
    private async Task SaveBusStopsAsync(RouteResponse routeData)
    {
        try
        {
            using var conn = _dbFactory.CreateConnection();
            
            var stopSql = @"
                INSERT INTO bus_stops (line_number, direction, stop_order, stop_name, updated_at)
                VALUES (@LineNumber, @Direction, @Order, @StopName, NOW())";

            foreach (var r in routeData.Routes)
            {
                var dir = r.RouteName;
                
                // Delete old stops for this route direction, then re-insert
                await conn.ExecuteAsync(
                    "DELETE FROM bus_stops WHERE line_number = @LineNumber AND direction = @Direction",
                    new { LineNumber = routeData.LineNumber, Direction = dir });
                
                foreach (var stop in r.BusStops)
                {
                    await conn.ExecuteAsync(stopSql, new
                    {
                        LineNumber = routeData.LineNumber,
                        Direction = dir,
                        Order = stop.Order,
                        StopName = stop.Name
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving bus stops for line {LineNumber}", routeData.LineNumber);
        }
    }

    private async Task NotifyChangesAsync(RouteResponse route, List<string> changes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🔄 *Güzergah Değişikliği: {TelegramHelper.EscapeMarkdown(route.LineNumber)} - {TelegramHelper.EscapeMarkdown(route.LineName)}*");
        sb.AppendLine();
        
        foreach (var change in changes)
        {
            sb.AppendLine(change);
        }

        sb.AppendLine(); 
        var url = $"https://ulasim.sakarya.bel.tr/ulasim/hat-detay/{route.LineId}";
        sb.AppendLine($"[Detaylı Bilgi]({url})");

        await _telegramHelper.SendMessageAsync(sb.ToString());

        try
        {
            var addedStops = changes.Where(c => c.Contains("Durak Eklendi")).Select(c => c.Trim()).ToList();
            var removedStops = changes.Where(c => c.Contains("Durak Kaldırıldı")).Select(c => c.Trim()).ToList();
            var embed = DiscordEmbedBuilder.RouteChanged(route.LineNumber, route.LineName, addedStops, removedStops);
            await _discordHelper.SendEmbedWithButtonAsync("SAKUS", "sakarya-ulasim", embed, "📍 Güzergahı Gör", url);
        }
        catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
    }

    private List<string> CompareRoutes(RouteResponse oldData, RouteResponse newData)
    {
        var changes = new List<string>();

        // Map routes by RouteId to compare same directions
        var oldRoutes = oldData.Routes.ToDictionary(r => r.RouteId);
        var newRoutes = newData.Routes.ToDictionary(r => r.RouteId);

        // Check for removed routes
        foreach (var oldR in oldRoutes)
        {
            if (!newRoutes.ContainsKey(oldR.Key))
            {
                changes.Add($"❌ *Yön Kaldırıldı*: {oldR.Value.RouteName}");
            }
        }

        // Check for added/modified routes
        foreach (var newR in newRoutes)
        {
            if (!oldRoutes.TryGetValue(newR.Key, out var oldRoute))
            {
                changes.Add($"🆕 *Yeni Yön Eklendi*: {newR.Value.RouteName}");
                continue;
            }

            // Compare stops
            var stopChanges = CompareStops(oldRoute, newR.Value);
            if (stopChanges.Count > 0)
            {
                changes.Add($"📍 *{newR.Value.RouteName} Yönü*:");
                changes.AddRange(stopChanges);
            }
        }

        return changes;
    }

    private List<string> CompareStops(RouteDetail oldRoute, RouteDetail newRoute)
    {
        var diffs = new List<string>();
        
        // Simple comparison: ordered lists
        // Note: Stop IDs are unique.
        
        var oldStops = oldRoute.BusStops.OrderBy(s => s.Order).ToList();
        var newStops = newRoute.BusStops.OrderBy(s => s.Order).ToList();

        // 1. Check for removed stops
        foreach (var oldStop in oldStops)
        {
            if (!newStops.Any(ns => ns.Id == oldStop.Id))
            {
                diffs.Add($"   ➖ Durak Kaldırıldı: _{oldStop.Name}_ (Sıra: {oldStop.Order})");
            }
        }

        // 2. Check for added stops
        foreach (var newStop in newStops)
        {
            if (!oldStops.Any(os => os.Id == newStop.Id))
            {
                diffs.Add($"   ➕ Durak Eklendi: _{newStop.Name}_ (Sıra: {newStop.Order})");
            }
        }

        // 3. Check for order changes (only if not added/removed)
        // This can be noisy if a stop is inserted, shifting all subsequent stops.
        // So maybe we skip "Order changed" messages if the list count changed?
        // Or just report if ID exists but Order is drastically different?
        // Let's keep it simple: only report add/removes for now as that's user critical.
        // Geometry changes are implicit in stop changes usually.

        return diffs;
    }

    private async Task<List<int>> FetchLineIdsAsync(int busType, CancellationToken token)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = string.Format(ListApiUrl, busType);
            
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
                request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                return await client.SendAsync(request, token);
            });

            if (!response.IsSuccessStatusCode) return new List<int>();

            var json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            
            var ids = new List<int>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        ids.Add(idProp.GetInt32());
                    }
                }
            }
            return ids;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching line list for type {busType}");
            return new List<int>();
        }
    }

    private async Task<RouteResponse?> FetchRouteDetailsAsync(int lineId, CancellationToken token)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            // Use tomorrow's date or just current date? 
            // The user used a future date in example. Let's use Today.
            // API format: yyyy-MM-ddT00:00:00.000Z
            var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd") + "T00:00:00.000Z";
            var url = string.Format(RouteApiUrl, lineId, dateStr);
            
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
                request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                return await client.SendAsync(request, token);
            });

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            return JsonSerializer.Deserialize<RouteResponse>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching route details for line {lineId}");
            return null;
        }
    }
}
