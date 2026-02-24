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
using System.Text.Json;

namespace SbbBot.Services;

public class BusLineWatcherService : BackgroundService
{
    private readonly ILogger<BusLineWatcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramHelper _telegramHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;

    private const string ApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim?busType={0}";
    private const int BusTypeBelediye = 3869;
    private const int BusTypeOzelHalk = 5731;
    private const int BusTypeTaksiDolmus = 5733;
    private const int BusTypeMinibus = 5732;

    public BusLineWatcherService(
        ILogger<BusLineWatcherService> logger,
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
                    _logger.LogWarning($"BusLineWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BusLineWatcherService started.");

        using var timer = new PeriodicTimer(TimeSpan.FromHours(_config.Intervals.BusLinesHours));

        do
        {
            try
            {
                await CheckBusLinesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BusLineWatcherService execution");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested);
    }



    private async Task CheckBusLinesAsync(CancellationToken cancellationToken)
    {
        // Skip if data was fetched recently
        var ageHours = StorageHelper.GetDataFileAgeHours("bus_lines.json");
        if (ageHours < _config.Intervals.BusLinesHours)
        {
            _logger.LogInformation("Bus lines data is fresh ({0:F1}h old), skipping check.", ageHours);
            return;
        }

        var fetchedOzelHalk = await FetchLinesFromApiAsync(BusTypeOzelHalk, cancellationToken);
        var fetchedBelediye = await FetchLinesFromApiAsync(BusTypeBelediye, cancellationToken);
        var fetchedTaksiDolmus = await FetchLinesFromApiAsync(BusTypeTaksiDolmus, cancellationToken);
        var fetchedMinibus = await FetchLinesFromApiAsync(BusTypeMinibus, cancellationToken);

        // Sort both lists
        fetchedOzelHalk = fetchedOzelHalk.Distinct().OrderBy(GetLineSortKey).ToList();
        fetchedBelediye = fetchedBelediye.Distinct().OrderBy(GetLineSortKey).ToList();
        fetchedTaksiDolmus = fetchedTaksiDolmus.Distinct().OrderBy(GetLineSortKey).ToList();
        fetchedMinibus = fetchedMinibus.Distinct().OrderBy(GetLineSortKey).ToList();

        var currentData = new BusLinesData
        {
            ozel_halk = fetchedOzelHalk,
            belediye = fetchedBelediye,
            taksi_dolmus = fetchedTaksiDolmus,
            minibus = fetchedMinibus
        };

        var storedData = await StorageHelper.ReadBusLinesAsync();
        bool changesDetected = false;

        // Compare Ozel Halk
        if (await ProcessLineChangesAsync(storedData.ozel_halk, currentData.ozel_halk, "Özel Halk Otobüsü"))
        {
            changesDetected = true;
        }

        // Compare Belediye (includes Metrobus/Adaray)
        if (await ProcessLineChangesAsync(storedData.belediye, currentData.belediye, "Belediye Otobüsü"))
        {
            changesDetected = true;
        }

        // Compare Taksi-Dolmuş
        if (await ProcessLineChangesAsync(storedData.taksi_dolmus, currentData.taksi_dolmus, "Taksi-Dolmuş"))
        {
            changesDetected = true;
        }

        // Compare Minibüs
        if (await ProcessLineChangesAsync(storedData.minibus, currentData.minibus, "Minibüs"))
        {
            changesDetected = true;
        }

        if (changesDetected)
        {
            await StorageHelper.SaveBusLinesAsync(currentData);
        }

        // New: Check for Schedule Changes
        // We do this sequentially to be polite to the server
        await CheckSchedulesAsync(currentData.belediye, "belediye-otobusleri", cancellationToken);
        await CheckSchedulesAsync(currentData.ozel_halk, "ozel-halk-otobusleri", cancellationToken);
        // No schedules for Taksi-Dolmus currently
    }

    private async Task CheckSchedulesAsync(List<string> lines, string subfolder, CancellationToken cancellationToken)
    {
        foreach (var lineEntry in lines)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var parts = lineEntry.Split('|');
            if (parts.Length < 1) continue;
            var name = parts[0];
            var url = parts.Length > 1 ? parts[1] : "";

            if (string.IsNullOrEmpty(url)) continue;

            try
            {
                // Polite delay
                await Task.Delay(1000, cancellationToken);

                var storedSchedule = await StorageHelper.ReadScheduleAsync(name, subfolder);
                if (storedSchedule == null)
                {
                    storedSchedule = new BusSchedule
                    {
                        LineName = name,
                        Url = url,
                        DayTimes = new Dictionary<string, List<string>>()
                    };
                }
                else
                {
                    // Skip if data was checked recently (within the configured interval)
                    var hoursSinceLastCheck = (DateTime.UtcNow - storedSchedule.LastChecked).TotalHours;
                    if (hoursSinceLastCheck < _config.Intervals.BusLinesHours)
                    {
                        continue;
                    }
                }

                // 1. Get LineId
                // Try to extract from URL first to avoid unnecessary HTML fetch
                if (storedSchedule.LineId == 0)
                {
                    storedSchedule.LineId = ExtractLineIdFromUrl(url);
                }

                string html = "";
                // Only fetch HTML if we still don't have the LineId
                if (storedSchedule.LineId == 0)
                {
                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    try 
                    {
                         html = await _retryPolicy.ExecuteAsync(async () => 
                            await client.GetStringAsync(url, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to fetch HTML for line {name}: {ex.Message}");
                    }

                    if (!string.IsNullOrEmpty(html))
                    {
                        storedSchedule.LineId = ExtractLineId(html);
                    }
                }

                if (storedSchedule.LineId == 0)
                {
                     // If still 0, we can't do anything
                     // Only log if we actually tried to fetch HTML and failed or it didn't help
                     if (!string.IsNullOrEmpty(html) || storedSchedule.LineId == 0)
                        _logger.LogWarning($"Could not extract LineId for {name}. Url: {url}");
                     continue;
                }

                // Check for announcements is now handled by AnnouncementWatcherService generically for all lines.
                // We no longer need to check per-line alerts here.


                // 2. Prepare Dates for API Calls
                var now = DateTime.UtcNow.AddHours(3); // Turkey Time
                var nextWeekday = GetNextDay(now, DayOfWeek.Monday); // Default to Monday
                // If today is weekday, use today
                if (now.DayOfWeek != DayOfWeek.Saturday && now.DayOfWeek != DayOfWeek.Sunday) nextWeekday = now;

                var nextSaturday = GetNextDay(now, DayOfWeek.Saturday);
                var nextSunday = GetNextDay(now, DayOfWeek.Sunday);

                // 3. Fetch New Schedules
                var newDayTimes = new Dictionary<string, List<string>>();

                var weekdayTimes = await FetchScheduleFromApiAsync(storedSchedule.LineId, nextWeekday, cancellationToken);
                if (weekdayTimes != null) newDayTimes["Hafta İçi"] = weekdayTimes;

                var saturdayTimes = await FetchScheduleFromApiAsync(storedSchedule.LineId, nextSaturday, cancellationToken);
                if (saturdayTimes != null) newDayTimes["Cumartesi"] = saturdayTimes;

                var sundayTimes = await FetchScheduleFromApiAsync(storedSchedule.LineId, nextSunday, cancellationToken);
                if (sundayTimes != null) newDayTimes["Pazar"] = sundayTimes;

                // 4. Compare and Save
                var newHash = ComputeScheduleHash(newDayTimes);
                bool hasChanged = storedSchedule.LastScheduleHash != newHash;

                if (hasChanged)
                {
                    // ... (existing logic)
                    if (!string.IsNullOrEmpty(storedSchedule.LastScheduleHash)) 
                    {
                        var diffMessage = GenerateDiffMessage(name, storedSchedule.DayTimes, newDayTimes);
                        if (!string.IsNullOrEmpty(diffMessage))
                        {
                            var msg = $"🚌 *{name} Hattı Saat Değişikliği*\n\n{diffMessage}\n🔗 [Tarifeye Git]({url})";
                            await _telegramHelper.SendMessageAsync(msg);
                        }
                    }

                    storedSchedule.DayTimes = newDayTimes;
                    storedSchedule.LastScheduleHash = newHash;
                    storedSchedule.LastChecked = DateTime.UtcNow;
                    await StorageHelper.SaveScheduleAsync(storedSchedule, subfolder);
                }
                else
                {
                    // Save every hour to refresh LastChecked
                    if ((DateTime.UtcNow - storedSchedule.LastChecked).TotalHours > 1)
                    {
                        storedSchedule.LastChecked = DateTime.UtcNow;
                        await StorageHelper.SaveScheduleAsync(storedSchedule, subfolder);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling schedule for {name}");
            }
        }
    }

    private int ExtractLineIdFromUrl(string url)
    {
        var match = Regex.Match(url, @"-(\d+)$");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
        {
            return id;
        }
        return 0;
    }

    private int ExtractLineId(string html)
    {
        // Look for "lineId": 123 inside the JSON script
        var match = Regex.Match(html, "\"lineId\"\\s*:\\s*(\\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
        {
            return id;
        }
        return 0;
    }

    private DateTime GetNextDay(DateTime start, DayOfWeek day)
    {
        int daysToAdd = ((int)day - (int)start.DayOfWeek + 7) % 7;
        // If today is the requested day, return today (daysToAdd will be 0)
        return start.AddDays(daysToAdd);
    }

    private async Task<List<string>?> FetchScheduleFromApiAsync(int lineId, DateTime date, CancellationToken token)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var dateStr = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var requestUrl = $"https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/line-schedule?date={dateStr}T00%3A00%3A00.000Z&lineId={lineId}";
            
            var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
            request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var response = await client.SendAsync(request, token);
            
            // Handle 204 No Content or other specific codes
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            if (string.IsNullOrWhiteSpace(json)) return null;

            try 
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                
                // Expected JSON: { "data": { "schedules": [ ... ] } }  OR root directly
                // From debug: {"lineId":2, ... "schedules": [...] }
                var root = doc.RootElement;
                JsonElement schedulesEl;

                // Try to find "schedules" property directly or nested in "data"
                if (root.TryGetProperty("data", out var dataEl) && dataEl.TryGetProperty("schedules", out var s1))
                {
                    schedulesEl = s1;
                }
                else if (root.TryGetProperty("schedules", out var s2))
                {
                    schedulesEl = s2;
                }
                else 
                {
                    return null;
                }

                var times = new List<string>();

                if (schedulesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var schedule in schedulesEl.EnumerateArray())
                    {
                        if (schedule.TryGetProperty("routeDetail", out var details) && details.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var detail in details.EnumerateArray())
                            {
                                if (detail.TryGetProperty("startTime", out var st))
                                {
                                    var t = st.GetString();
                                    if (!string.IsNullOrEmpty(t))
                                    {
                                        // "07:05:00" -> "07:05"
                                        if (TimeSpan.TryParse(t, out var ts))
                                            times.Add(ts.ToString(@"hh\:mm"));
                                        else
                                            times.Add(t);
                                    }
                                }
                            }
                        }
                    }
                }

                return times.Distinct().OrderBy(t => t).ToList();
            }
            catch (System.Text.Json.JsonException)
            {
                // Invalid JSON (e.g. HTML response), treat as no schedule
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"API fetch failed for line {lineId} date {date:yyyy-MM-dd}: {ex.Message}");
            return null;
        }
    }

    private string ComputeScheduleHash(Dictionary<string, List<string>> dayTimes)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var key in dayTimes.Keys.OrderBy(k => k))
        {
            sb.Append(key).Append(":");
            if (dayTimes[key] != null)
            {
                sb.Append(string.Join(",", dayTimes[key]));
            }
            sb.Append("|");
        }
        
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(bytes);
    }

    private string GenerateDiffMessage(string lineName, Dictionary<string, List<string>> oldDayTimes, Dictionary<string, List<string>> newDayTimes)
    {
        var sb = new System.Text.StringBuilder();
        
        foreach (var day in new[] { "Hafta İçi", "Cumartesi", "Pazar" })
        {
            // If new data doesn't have the key, skip (maybe API failed)
            if (!newDayTimes.ContainsKey(day)) continue;

            var newTimes = newDayTimes[day];
            var oldTimes = oldDayTimes != null && oldDayTimes.ContainsKey(day) ? oldDayTimes[day] : new List<string>();

            // Special Case: All trips removed (Service Cancelled for that day)
            if (oldTimes.Count > 0 && newTimes.Count == 0)
            {
                sb.AppendLine($"📅 *{day}*:");
                sb.AppendLine("   ❌ _Bu gün için tüm seferler kaldırıldı._");
                sb.AppendLine();
                continue;
            }

            // Special Case: Service Added for a previously empty day
            if (oldTimes.Count == 0 && newTimes.Count > 0)
            {
                 sb.AppendLine($"📅 *{day}*:");
                 sb.AppendLine($"   ➕ _Seferler eklendi:_ {string.Join(", ", newTimes)}");
                 sb.AppendLine();
                 continue;
            }

            // Special Case: Persistently Empty (No Service)
            // User request: "eğer en başından beri o günde sefer yoksa direkt [gün] Günleri Sefer Düzenlenmemektedir! ibaresi yer alsın"
            if (oldTimes.Count == 0 && newTimes.Count == 0)
            {
                sb.AppendLine($"📅 *{day}*:");
                sb.AppendLine($"   ⚠️ _{day} günleri sefer düzenlenmemektedir!_");
                sb.AppendLine();
                continue;
            }

            // Normal Diff
            var added = newTimes.Except(oldTimes).ToList();
            var removed = oldTimes.Except(newTimes).ToList();

            if (added.Any() || removed.Any())
            {
                sb.AppendLine($"📅 *{day}*:");
                if (added.Any()) sb.AppendLine($"   ➕ {string.Join(", ", added)}");
                if (removed.Any()) sb.AppendLine($"   ➖ {string.Join(", ", removed)}");
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }

    private async Task<List<string>> FetchLinesFromApiAsync(int busRequestTypeId, CancellationToken cancellationToken)
    {
        var url = string.Format(ApiUrl, busRequestTypeId);
        var client = _httpClientFactory.CreateClient();
        
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
        request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        try
        {
            var response = await _retryPolicy.ExecuteAsync(async () => 
                await client.SendAsync(request, cancellationToken));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"API request failed for busType {busRequestTypeId}: {response.StatusCode}");
                return new List<string>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.ValueKind != JsonValueKind.Array) return new List<string>();

            var lines = new List<string>();

            foreach (var item in root.EnumerateArray())
            {
                // Fields we need:
                // "lineNumber": "26", "name": "D. MEYDANI - SERDİVAN HAST. - KAMPÜS", "slug": "..."
                
                var lineNum = item.GetProperty("lineNumber").GetString()?.Trim() ?? "";
                var nameStr = item.GetProperty("name").GetString()?.Trim() ?? "";
                var slug    = item.TryGetProperty("slug", out var s) ? s.GetString() : "";

                if (string.IsNullOrEmpty(lineNum) && string.IsNullOrEmpty(nameStr)) continue;

                // Format Name
                var culture = new System.Globalization.CultureInfo("tr-TR");
                nameStr = culture.TextInfo.ToTitleCase(nameStr.ToLower(culture));
                
                // Construct display text: "26 - D. Meydani..."
                var fullText = $"{lineNum} - {nameStr}";
                
                // Construct URL
                // https://ulasim.sakarya.bel.tr/ulasim/{slug}
                var lineUrl = !string.IsNullOrEmpty(slug) 
                    ? $"https://ulasim.sakarya.bel.tr/ulasim/{slug}" 
                    : "";

                lines.Add($"{fullText}|{lineUrl}");
            }
            
            return lines;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching lines from API (type {busRequestTypeId})");
            return new List<string>();
        }
    }

    private (int Type, int Number, string? Suffix, string? FullText) GetLineSortKey(string lineEntry)
    {
        // lineEntry: "19K - Name|URL"
        // Extract the code part before first dash
        var parts = lineEntry.Split('|');
        var namePart = parts[0];
        var codePart = namePart.Split('-')[0].Trim();

        // 1. Check strict numeric start
        var match = Regex.Match(codePart, @"^(\d+)(.*)$");
        if (match.Success)
        {
            int number = int.Parse(match.Groups[1].Value);
            string suffix = match.Groups[2].Value.Trim();
            // Type 1: Numeric-led
            return (1, number, suffix, null);
        }

        // 2. Non-numeric start (A1, M1, ADARAY, METROBÜS)
        // Usually these should come either before or after standard numeric lines.
        // Let's put them at the end (Type 2) sorted alphabetically
        return (2, 0, null, codePart);
    }



    private async Task<bool> ProcessLineChangesAsync(List<string> oldLines, List<string> newLines, string category)
    {
        // First run Check
        if (oldLines == null || oldLines.Count == 0)
        {
            return true;
        }

        var oldSet = new HashSet<string>(oldLines);
        var newSet = new HashSet<string>(newLines);
        bool hasChanges = false;

        foreach (var line in newSet)
        {
            if (!oldSet.Contains(line))
            {
                var parts = line.Split('|');
                var name = parts[0];
                var url = parts.Length > 1 ? parts[1] : "";

                // Send markdown link if URL exists
                if (!string.IsNullOrEmpty(url))
                    await _telegramHelper.SendMessageAsync($"🆕 *Yeni Hat Eklendi ({category})*\n\n🚍 [{name}]({url})");
                else
                    await _telegramHelper.SendMessageAsync($"🆕 *Yeni Hat Eklendi ({category})*\n\n🚍 {name}");
                    
                hasChanges = true;
            }
        }

        foreach (var line in oldSet)
        {
            if (!newSet.Contains(line))
            {
                var parts = line.Split('|');
                var name = parts[0];
                await _telegramHelper.SendMessageAsync($"❌ *Hat Kaldırıldı ({category})*\n\n🚍 {name}");
                hasChanges = true;
            }
        }

        return hasChanges;
    }

}
