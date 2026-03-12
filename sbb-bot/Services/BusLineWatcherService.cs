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
    private readonly IDiscordHelper _discordHelper;
    private readonly BotConfig _config;
    private readonly AsyncRetryPolicy _retryPolicy;

    private const string ApiUrl = "https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim?busType={0}";
    private const int BusTypeBelediye = 3869;
    private const int BusTypeOzelHalk = 5731;
    private const int BusTypeTaksiDolmus = 5733;
    private const int BusTypeMinibus = 5732;

    public class FetchedLine
    {
        public int ApiId { get; set; }
        public string LineNumber { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Category { get; set; } = "";
    }

    private readonly SbbBot.Repositories.BusLineRepository _repository;

    public BusLineWatcherService(
        ILogger<BusLineWatcherService> logger,
        IHttpClientFactory httpClientFactory,
        TelegramHelper telegramHelper,
        IDiscordHelper discordHelper,
        IOptions<BotConfig> config,
        SbbBot.Repositories.BusLineRepository repository)
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
                    _logger.LogWarning($"BusLineWatcherService HTTP request failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BusLineWatcherService started.");

        int intervalHours = _config.Intervals.BusLinesHours > 0 ? _config.Intervals.BusLinesHours : 24;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

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
        var fetchedOzelHalk = await FetchLinesFromApiAsync(BusTypeOzelHalk, "Özel Halk Otobüsü", cancellationToken);
        var fetchedBelediye = await FetchLinesFromApiAsync(BusTypeBelediye, "Belediye Otobüsü", cancellationToken);
        var fetchedTaksiDolmus = await FetchLinesFromApiAsync(BusTypeTaksiDolmus, "Taksi-Dolmuş", cancellationToken);
        var fetchedMinibus = await FetchLinesFromApiAsync(BusTypeMinibus, "Minibüs", cancellationToken);

        var allFetched = new List<FetchedLine>();
        allFetched.AddRange(fetchedOzelHalk);
        allFetched.AddRange(fetchedBelediye);
        allFetched.AddRange(fetchedTaksiDolmus);
        allFetched.AddRange(fetchedMinibus);

        var isFirstLoad = !await _repository.IsSeededAsync();
        var allDbLines = await _repository.GetAllAsync();
        var dbLinesDict = allDbLines.ToDictionary(l => l.LineNumber, l => l);
        var fetchedLinesDict = allFetched.GroupBy(l => l.LineNumber).ToDictionary(g => g.Key, g => g.First());

        // Check for new lines
        foreach (var fetched in fetchedLinesDict.Values)
        {
            if (!dbLinesDict.TryGetValue(fetched.LineNumber, out var existingDb))
            {
                // New Line Added
                if (!isFirstLoad)
                {
                    var escapedName = $"{TelegramHelper.EscapeMarkdown(fetched.LineNumber)} - {TelegramHelper.EscapeMarkdown(fetched.Name)}";
                    var urlLink = !string.IsNullOrEmpty(fetched.Url) ? $"[{escapedName}]({fetched.Url})" : escapedName;
                    await _telegramHelper.SendMessageAsync($"🆕 *Yeni Hat Eklendi ({TelegramHelper.EscapeMarkdown(fetched.Category)})*\n\n🚍 {urlLink}");

                    try
                    {
                        var embed = DiscordEmbedBuilder.BusLineAdded(fetched.LineNumber, fetched.Name, fetched.Category);
                        var buttonUrl = fetched.Url;
                        await _discordHelper.SendEmbedWithButtonAsync("SAKUS", "sakarya-ulasim", embed, "🚌 Hat Sayfası", buttonUrl);
                    }
                    catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
                }

                var newDbLine = new SbbBot.Models.BusLine
                {
                    ApiId = fetched.ApiId,
                    LineNumber = fetched.LineNumber,
                    LineName = fetched.Name,
                    BusType = fetched.Category,
                    RawJson = ""
                };
                await _repository.UpsertAsync(newDbLine);
                dbLinesDict[fetched.LineNumber] = newDbLine;
            }
            else
            {
                // Verify if Name or Category changed (Optional, usually they don't change often but we should update DB)
                if (existingDb.LineName != fetched.Name || existingDb.BusType != fetched.Category || existingDb.ApiId != fetched.ApiId)
                {
                    existingDb.LineName = fetched.Name;
                    existingDb.BusType = fetched.Category;
                    existingDb.ApiId = fetched.ApiId;
                    await _repository.UpsertAsync(existingDb);
                }
            }
        }

        // Check for removed lines
        foreach (var dbLine in allDbLines)
        {
            if (!fetchedLinesDict.ContainsKey(dbLine.LineNumber))
            {
                // Line Removed
                if (!isFirstLoad)
                {
                    await _telegramHelper.SendMessageAsync($"❌ *Hat Kaldırıldı ({TelegramHelper.EscapeMarkdown(dbLine.BusType)})*\n\n🚍 {TelegramHelper.EscapeMarkdown(dbLine.LineNumber)} - {TelegramHelper.EscapeMarkdown(dbLine.LineName)}");

                    try
                    {
                        var embed = DiscordEmbedBuilder.BusLineRemoved(dbLine.LineNumber, dbLine.LineName);
                        await _discordHelper.SendEmbedAsync("SAKUS", "sakarya-ulasim", embed);
                    }
                    catch (Exception ex) { _logger.LogWarning("[Discord] Gönderilemedi: {Ex}", ex.Message); }
                }
                await _repository.DeleteAsync(dbLine.LineNumber);
            }
        }

        if (isFirstLoad)
        {
            await _repository.MarkAsSeededAsync();
            _logger.LogInformation("[{Servis}] İlk yükleme tamamlandı, bildirim atlanıyor.", nameof(BusLineWatcherService));
        }

        // Check for Schedule Changes (Sequential)
        await CheckSchedulesAsync(fetchedBelediye, cancellationToken);
        await CheckSchedulesAsync(fetchedOzelHalk, cancellationToken);
    }

    private async Task CheckSchedulesAsync(List<FetchedLine> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (string.IsNullOrEmpty(line.Url)) continue;

            try
            {
                await Task.Delay(1000, cancellationToken);

                var dbLine = await _repository.GetByLineNumberAsync(line.LineNumber);
                if (dbLine == null) continue;

                BusSchedule? storedSchedule = null;
                if (!string.IsNullOrEmpty(dbLine.RawJson))
                {
                    try
                    {
                        storedSchedule = JsonSerializer.Deserialize<BusSchedule>(dbLine.RawJson);
                    }
                    catch { }
                }

                if (storedSchedule == null)
                {
                    storedSchedule = new BusSchedule
                    {
                        LineName = $"{line.LineNumber} - {line.Name}",
                        Url = line.Url,
                        DayTimes = new Dictionary<string, List<string>>(),
                        LastChecked = DateTime.UtcNow
                    }; // Assuming first load check or we update later
                }
                else
                {
                    var hoursSinceLastCheck = (DateTime.UtcNow - storedSchedule.LastChecked).TotalHours;
                    if (hoursSinceLastCheck < _config.Intervals.BusLinesHours)
                    {
                        continue;
                    }
                }

                if (storedSchedule.LineId == 0)
                {
                    storedSchedule.LineId = ExtractLineIdFromUrl(line.Url);
                }

                string html = "";
                if (storedSchedule.LineId == 0)
                {
                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    try 
                    {
                         html = await _retryPolicy.ExecuteAsync(async () => 
                            await client.GetStringAsync(line.Url, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to fetch HTML for line {line.LineNumber}: {ex.Message}");
                    }

                    if (!string.IsNullOrEmpty(html))
                    {
                        storedSchedule.LineId = ExtractLineId(html);
                    }
                }

                if (storedSchedule.LineId == 0)
                {
                     if (!string.IsNullOrEmpty(html) || storedSchedule.LineId == 0)
                        _logger.LogWarning($"Could not extract LineId for {line.LineNumber}. Url: {line.Url}");
                     continue;
                }

                var now = DateTime.UtcNow.AddHours(3);
                var nextWeekday = GetNextDay(now, DayOfWeek.Monday);
                if (now.DayOfWeek != DayOfWeek.Saturday && now.DayOfWeek != DayOfWeek.Sunday) nextWeekday = now;

                var nextSaturday = GetNextDay(now, DayOfWeek.Saturday);
                var nextSunday = GetNextDay(now, DayOfWeek.Sunday);

                var newDayTimes = new Dictionary<string, List<string>>();

                var weekdayTimes = await FetchScheduleFromApiAsync(storedSchedule.LineId, nextWeekday, cancellationToken);
                if (weekdayTimes != null) newDayTimes["Hafta İçi"] = weekdayTimes;

                var saturdayTimes = await FetchScheduleFromApiAsync(storedSchedule.LineId, nextSaturday, cancellationToken);
                if (saturdayTimes != null) newDayTimes["Cumartesi"] = saturdayTimes;

                var sundayTimes = await FetchScheduleFromApiAsync(storedSchedule.LineId, nextSunday, cancellationToken);
                if (sundayTimes != null) newDayTimes["Pazar"] = sundayTimes;

                var newHash = ComputeScheduleHash(newDayTimes);
                bool hasChanged = storedSchedule.LastScheduleHash != newHash;

                if (hasChanged)
                {
                    if (!string.IsNullOrEmpty(storedSchedule.LastScheduleHash)) 
                    {
                        var diffMessage = GenerateDiffMessage($"{line.LineNumber} - {line.Name}", storedSchedule.DayTimes, newDayTimes);
                        if (!string.IsNullOrEmpty(diffMessage))
                        {
                            // "İlk yükleme sonrasında" checks happen implicitly, since on first load LastScheduleHash will be empty.
                            var msg = $"🚌 *{line.LineNumber} - {line.Name} Hattı Saat Değişikliği*\n\n{diffMessage}\n🔗 [Tarifeye Git]({line.Url})";
                            await _telegramHelper.SendMessageAsync(msg);
                        }
                    }

                    storedSchedule.DayTimes = newDayTimes;
                    storedSchedule.LastScheduleHash = newHash;
                    storedSchedule.LastChecked = DateTime.UtcNow;

                    dbLine.RawJson = JsonSerializer.Serialize(storedSchedule);
                    await _repository.UpsertAsync(dbLine);
                }
                else
                {
                    if ((DateTime.UtcNow - storedSchedule.LastChecked).TotalHours > 1)
                    {
                        storedSchedule.LastChecked = DateTime.UtcNow;
                        dbLine.RawJson = JsonSerializer.Serialize(storedSchedule);
                        await _repository.UpsertAsync(dbLine);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling schedule for {line.LineNumber}");
            }
        }
    }

    private int ExtractLineIdFromUrl(string url)
    {
        var match = Regex.Match(url, @"-(\d+)$");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int id)) return id;
        return 0;
    }

    private int ExtractLineId(string html)
    {
        var match = Regex.Match(html, "\"lineId\"\\s*:\\s*(\\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int id)) return id;
        return 0;
    }

    private DateTime GetNextDay(DateTime start, DayOfWeek day)
    {
        int daysToAdd = ((int)day - (int)start.DayOfWeek + 7) % 7;
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
            
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            if (string.IsNullOrWhiteSpace(json)) return null;

            try 
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                JsonElement schedulesEl;

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
            if (dayTimes[key] != null) sb.Append(string.Join(",", dayTimes[key]));
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
            if (!newDayTimes.ContainsKey(day)) continue;

            var newTimes = newDayTimes[day];
            var oldTimes = oldDayTimes != null && oldDayTimes.ContainsKey(day) ? oldDayTimes[day] : new List<string>();

            if (oldTimes.Count > 0 && newTimes.Count == 0)
            {
                sb.AppendLine($"📅 *{day}*:");
                sb.AppendLine("   ❌ _Bu gün için tüm seferler kaldırıldı._");
                sb.AppendLine();
                continue;
            }

            if (oldTimes.Count == 0 && newTimes.Count > 0)
            {
                 sb.AppendLine($"📅 *{day}*:");
                 sb.AppendLine($"   ➕ _Seferler eklendi:_ {string.Join(", ", newTimes)}");
                 sb.AppendLine();
                 continue;
            }

            if (oldTimes.Count == 0 && newTimes.Count == 0)
            {
                sb.AppendLine($"📅 *{day}*:");
                sb.AppendLine($"   ⚠️ _{day} günleri sefer düzenlenmemektedir!_");
                sb.AppendLine();
                continue;
            }

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

    private async Task<List<FetchedLine>> FetchLinesFromApiAsync(int busRequestTypeId, string category, CancellationToken cancellationToken)
    {
        var url = string.Format(ApiUrl, busRequestTypeId);
        var client = _httpClientFactory.CreateClient();

        try
        {
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
                request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                return await client.SendAsync(request, cancellationToken);
            });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"API request failed for busType {busRequestTypeId}: {response.StatusCode}");
                return new List<FetchedLine>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json)) return new List<FetchedLine>();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.ValueKind != JsonValueKind.Array) return new List<FetchedLine>();

            var lines = new List<FetchedLine>();

            foreach (var item in root.EnumerateArray())
            {
                var apiId   = item.TryGetProperty("id",   out var idProp) ? idProp.GetInt32() : 0;
                var lineNum = item.GetProperty("lineNumber").GetString()?.Trim() ?? "";
                var nameStr = item.GetProperty("name").GetString()?.Trim() ?? "";
                var slug    = item.TryGetProperty("slug", out var s) ? s.GetString() : "";

                if (string.IsNullOrEmpty(lineNum) && string.IsNullOrEmpty(nameStr)) continue;

                var culture = new System.Globalization.CultureInfo("tr-TR");
                nameStr = culture.TextInfo.ToTitleCase(nameStr.ToLower(culture));
                
                var lineUrl = !string.IsNullOrEmpty(slug) ? $"https://ulasim.sakarya.bel.tr/ulasim/{slug}" : "";

                lines.Add(new FetchedLine
                {
                    ApiId = apiId,
                    LineNumber = lineNum,
                    Name = nameStr,
                    Url = lineUrl,
                    Category = category
                });
            }
            
            return lines;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching lines from API (type {busRequestTypeId})");
            return new List<FetchedLine>();
        }
    }
}
