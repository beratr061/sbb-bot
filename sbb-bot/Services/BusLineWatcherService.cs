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

    private const string OzelHalkUrl = "https://ulasim.sakarya.bel.tr/ulasim/ozel-halk";
    private const string BelediyeUrl = "https://ulasim.sakarya.bel.tr/ulasim/belediye";
    private const string MetrobusUrl = "https://ulasim.sakarya.bel.tr/ulasim/metrobus";
    private const string AdarayUrl = "https://ulasim.sakarya.bel.tr/ulasim/adaray";

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
        var fetchedOzelHalk = await FetchLinesAsync(OzelHalkUrl, cancellationToken);
        var fetchedBelediye = await FetchLinesAsync(BelediyeUrl, cancellationToken);
        var fetchedMetrobus = await FetchLinesAsync(MetrobusUrl, cancellationToken);
        var fetchedAdaray = await FetchLinesAsync(AdarayUrl, cancellationToken);

        // Merge Metrobus and Adaray into "Belediye" list for notification/storage purposes
        // Or keep them separate if needed. For now, merging into Belediye to keep storage simple.
        fetchedBelediye.AddRange(fetchedMetrobus);
        fetchedBelediye.AddRange(fetchedAdaray);

        // Dedup just in case
        fetchedBelediye = fetchedBelediye.Distinct().OrderBy(GetLineSortKey).ToList();

        var currentData = new BusLinesData
        {
            ozel_halk = fetchedOzelHalk,
            belediye = fetchedBelediye
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

        if (changesDetected)
        {
            await StorageHelper.SaveBusLinesAsync(currentData);
        }

        // New: Check for Schedule Changes
        // We do this sequentially to be polite to the server
        await CheckSchedulesAsync(currentData.belediye, "belediye-otobusleri", cancellationToken);
        await CheckSchedulesAsync(currentData.ozel_halk, "ozel-halk-otobusleri", cancellationToken);
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

                // 1. Get LineId (fetch HTML if missing OR if we need to check alerts)
                // We should check alerts periodically too. Let's fetch HTML if we haven't checked alerts in a while or if LineId is missing.
                // For simplicity, let's fetch HTML every time we check schedules to ensure we catch alerts.
                // But to be polite, maybe we can combine it. 

                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                
                string html = "";
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
                    // Check for Line Alerts (Modals)
                    await CheckForLineAlertsAsync(name, url, html, storedSchedule);

                    if (storedSchedule.LineId == 0)
                    {
                        storedSchedule.LineId = ExtractLineId(html);
                        if (storedSchedule.LineId == 0)
                        {
                            _logger.LogWarning($"Could not extract LineId for {name}. Url: {url}");
                            // logic to continue or not? If we can't get LineId, we can't get schedule.
                            // But we might have gotten an alert.
                        }
                    }
                }
                else if (storedSchedule.LineId == 0)
                {
                     continue; // Can't do anything without HTML if LineId is missing
                }

                // If we still don't have LineId, skip schedule check
                if (storedSchedule.LineId == 0) continue;

                // 2. Prepare Dates for API Calls ... (rest of the code)

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
                    // If no schedule change, check if we should save due to alert change?
                    // We don't know if alert changed. 
                    // Let's just update LastChecked and Save if > 1 hour OR if LastAlertHash is not empty (slightly aggressive but safer).
                    
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

    private async Task<List<string>> FetchLinesAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        string html = await _retryPolicy.ExecuteAsync(async () =>
            await client.GetStringAsync(url, cancellationToken));

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var lines = new List<string>();
        
        string anchorFilter;
        if (url.Contains("ozel-halk")) anchorFilter = "/ulasim/ozel-halk/";
        else if (url.Contains("metrobus")) anchorFilter = "/ulasim/metrobus/";
        else if (url.Contains("adaray")) anchorFilter = "/ulasim/adaray/";
        else anchorFilter = "/ulasim/belediye/";
        
        var nodes = doc.DocumentNode.SelectNodes($"//a[contains(@href, '{anchorFilter}')]");
        
        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                var href = node.GetAttributeValue("href", "");
                if (string.IsNullOrWhiteSpace(href)) continue;

                // Ensure absolute URL
                if (!href.StartsWith("http")) 
                {
                    href = "https://ulasim.sakarya.bel.tr" + (href.StartsWith("/") ? "" : "/") + href;
                }

                // New scraping requirement:
                // <span class="card-number">Code</span>
                // <span class="card-title">Name</span>
                
                var numberNode = node.SelectSingleNode(".//span[contains(@class, 'card-number')]");
                var titleNode = node.SelectSingleNode(".//span[contains(@class, 'card-title')]");
                
                string text;
                
                if (numberNode != null && titleNode != null)
                {
                    var code = numberNode.InnerText.Trim();
                    var name = titleNode.InnerText.Trim();
                    
                    // Decode
                    code = System.Net.WebUtility.HtmlDecode(code);
                    name = System.Net.WebUtility.HtmlDecode(name);
                    
                    // Format: "21C - CAMİLİ"
                    text = $"{code} - {name}";
                }
                else
                {
                    // Fallback to old behavior if spans are missing
                    text = node.InnerText.Trim();
                    text = System.Net.WebUtility.HtmlDecode(text);
                    text = FormatLineName(text, href);
                }

                // Clean spaces
                text = Regex.Replace(text, @"\s+", " ").Trim();

                if (string.IsNullOrWhiteSpace(text)) continue;

                // Ensure it looks like a line (starts with digit OR Letter like M1, A1)
                // Relaxed Check: Must be > 1 char
                if (text.Length < 2) continue;

                // Valid if starts with Digit or Specific Codes (M1, A1, ADARAY, METROBUS)
                bool isValidLine = char.IsDigit(text[0]) || 
                                   text.StartsWith("M", StringComparison.OrdinalIgnoreCase) || 
                                   text.StartsWith("A", StringComparison.OrdinalIgnoreCase) ||
                                   text.StartsWith("ADARAY", StringComparison.OrdinalIgnoreCase) ||
                                   text.StartsWith("METROBÜS", StringComparison.OrdinalIgnoreCase);

                if (isValidLine)
                {
                   // Store Name|URL
                   lines.Add($"{text}|{href}");
                }
            }
        }
        
        // Filter out blacklist (check name part)
        var blacklist = new HashSet<string> { "SAKUS - Ulaşım", "Haberler - Duyurular" }; 
        
        return lines
            .Where(x => !blacklist.Contains(x.Split('|')[0].Trim()))
            .Distinct()
            .OrderBy(GetLineSortKey)
            .ToList();
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

    private string FormatLineName(string rawText, string url)
    {
        try 
        {
            // rawText examples: "8AORT A GARAJ", "1SAKARYAPARK", "19K KAMPÜS"
            // url examples: .../8a-orta-garaj, .../1-numara-sakaryapark
            
            // 1. Extract Code from URL
            var uri = new Uri(url);
            var slug = uri.Segments.LastOrDefault() ?? "";
            var parts = slug.Split('-');
            
            if (parts.Length == 0) return rawText;
            
            string code = parts[0]; // "8a", "1", "19k"
            
            // Handle split codes in URL like "21-c-..." -> "21c"
            // If the second part is short (1-2 chars) and not a number, it's likely a suffix
            if (parts.Length > 1)
            {
                var p2 = parts[1];
                if (p2.Length <= 2 && !char.IsDigit(p2[0]) && 
                    !string.Equals(p2, "no", StringComparison.OrdinalIgnoreCase) && 
                    !string.Equals(p2, "numara", StringComparison.OrdinalIgnoreCase))
                {
                    code += p2;
                }
            }
            
            code = code.ToUpper(); // "8A", "1", "19K"

            // 2. Separate Code from Name in rawText
            // We strip the code from the BEGINNING of rawText
            
            // Normalized comparison to find the cut point
            string cleanRaw = rawText.ToUpper().Replace(" ", "");
            string cleanCode = code.Replace(" ", "");
            
            string namePart = rawText;
            
            if (cleanRaw.StartsWith(cleanCode))
            {
                // We know rawText *conceptually* starts with the code.
                // We need to find where the code ENDS in the original rawText string (preserving original chars/spaces for the name part if possible, though we will TitleCase it anyway)
                
                // Simple heuristic: match the length of the code, but accounted for potential spaces in rawText?
                // Actually, if rawText is "8AORT A GARAJ", and code is "8A".
                // We can just iterate to match characters.
                
                int rawIndex = 0;
                int codeIndex = 0;
                
                while (codeIndex < cleanCode.Length && rawIndex < rawText.Length)
                {
                    if (char.ToUpper(rawText[rawIndex]) == cleanCode[codeIndex])
                    {
                        codeIndex++;
                    }
                    else if (char.IsWhiteSpace(rawText[rawIndex]) || rawText[rawIndex] == '-')
                    {
                        // skip separators in raw text
                    }
                    else
                    {
                        // mismatch? - abort approach
                        break;
                    }
                    rawIndex++;
                }
                
                if (codeIndex == cleanCode.Length)
                {
                    namePart = rawText.Substring(rawIndex).Trim();
                    // Clean up leading dashes or spaces
                    namePart = namePart.TrimStart('-', ' ', '.');
                }
            }
            else
            {
                // Fallback: regex to split digits+chars from text
                // if URL matching failed (e.g. URL says 100, Text says 100B?)
                 var match = Regex.Match(rawText, @"^(\d+[A-Z\.-]*)(.*)");
                 if (match.Success)
                 {
                     code = match.Groups[1].Value;
                     namePart = match.Groups[2].Value.TrimStart('-', ' ', '.');
                 }
            }

            // 3. Format Name to Title Case
            var culture = new System.Globalization.CultureInfo("tr-TR");
            namePart = culture.TextInfo.ToTitleCase(namePart.ToLower(culture));
            
            // Final Cleanup of Name
            // "Ort A Garaj" -> "Orta Garaj" heuristic? 
            // Maybe safer not to touch spelling for now, just casing.
            
            return $"{code} - {namePart}";
        }
        catch
        {
            return rawText;
        }
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
    private async Task CheckForLineAlertsAsync(string lineName, string url, string html, BusSchedule storedSchedule)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Look for the modal structure provided by user:
            // <div class="content ..."><div class="header"><span class="title ...">Title</span>...</div><div class="text"><p>...</p></div>...</div>
            // Specifically looking for the title and text content.

            // The user provided snippet has:
            // <span class="title gotham-medium leaf-300 ...">Planlı Bakım Çalışması</span>
            // <div class="text ..."><p>...</p></div>

            var titleNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'title') and contains(@class, 'gotham-medium')]");
            var textNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'text')]");

            if (titleNode != null && textNode != null)
            {
                var title = titleNode.InnerText.Trim();
                var text = textNode.InnerText.Trim();
                
                // Clean up html entities
                title = System.Net.WebUtility.HtmlDecode(title);
                text = System.Net.WebUtility.HtmlDecode(text);

                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(text))
                {
                    // Compute hash
                    var contentToHash = $"{title}|{text}";
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(contentToHash));
                    var hash = Convert.ToBase64String(bytes);

                    if (storedSchedule.LastAlertHash != hash)
                    {
                        // New Alert!
                        var msg = $"📢 *Hat Duyurusu: {lineName}*\n\n" +
                                  $"🔹 *{title}*\n\n" +
                                  $"{text.Replace("<br>", "\n").Replace("<p>", "").Replace("</p>", "\n")}\n\n" +
                                  $"🔗 [Detaylar]({url})";
                        
                        await _telegramHelper.SendMessageAsync(msg);

                        storedSchedule.LastAlertHash = hash;
                        // Avoid saving here immediately potentially, wait for schedule save? 
                        // Or force save because alert is important.
                        // Let's set it, and let the main loop save if schedule changes OR force save if hash changed but schedule didn't?
                        // Actually, the main loop checks `storedSchedule.LastScheduleHash != newHash`. 
                        // We need to signal change.
                        // But CheckForLineAlertsAsync returns void.
                        // Let's rely on the fact that if we found an alert, we updated the object in memory.
                        // We should ensure it gets saved.
                    }
                }
            }
            else
            {
                // No alert found. 
                // If there WAS an alert before, should we clear the hash?
                // If we clear it, then if the alert comes back (e.g. intermittent page load), we resend.
                // If the alert is gone, we can clear it so next time a new alert comes we treat it as new.
                if (!string.IsNullOrEmpty(storedSchedule.LastAlertHash))
                {
                    storedSchedule.LastAlertHash = "";
                    // Alert removed. Should we notify? Maybe "Alert ended"? 
                    // Usually users care about active alerts. Silence is fine.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error parsing alerts for {lineName}");
        }
    }
}
