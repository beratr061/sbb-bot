using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using SbbBot.Helpers;
using SbbBot.Models;
using System.Text;
using System.Text.Json;

namespace SbbBot.Services;

public class InteractionManager
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<InteractionManager> _logger;
    private readonly VehicleService _vehicleService;
    private readonly IHttpClientFactory _httpClientFactory;

    // Cache line list in memory for quick search
    private List<LineItem> _cachedLines = new();
    private DateTime _lastCacheTime = DateTime.MinValue;
    
    public InteractionManager(
        ITelegramBotClient botClient,
        ILogger<InteractionManager> logger,
        IHttpClientFactory httpClientFactory,
        VehicleService vehicleService)
    {
        _botClient = botClient;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _vehicleService = vehicleService;
    }

    // ... (existing code omitted)

    public async Task HandleMessageAsync(Message message, CancellationToken token)
    {
        var text = message.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (text.Equals("/start", StringComparison.OrdinalIgnoreCase) || text.Equals("/yardım", StringComparison.OrdinalIgnoreCase))
        {
            await ShowMainMenuAsync(message.Chat.Id, token);
        }
        else if (text.StartsWith("/hat", StringComparison.OrdinalIgnoreCase))
        {
            await ShowLineCategoriesAsync(message.Chat.Id, token);
        }
        else
        {
            // Simple search if it looks like a line number
            if (text.Length < 10)
            {
                await SearchLineAsync(message.Chat.Id, text, token);
            }
        }
    }

    // --- MENUS ---

    private async Task ShowMainMenuAsync(long chatId, CancellationToken token, int? updateMessageId = null)
    {
        var text = "👋 **SBB Ulaşım Asistanına Hoşgeldiniz!**\n\n" +
                   "Sakarya toplu taşıma araçları hakkında anlık bilgi alabilirsiniz. Ne yapmak istersiniz?";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new [] { InlineKeyboardButton.WithCallbackData("🚌 Hat Sorgula", "cmd_hat_secim") },
            // new [] { InlineKeyboardButton.WithCallbackData("🚏 Durak Sorgula (Yakında)", "cmd_durak") },
            new [] { InlineKeyboardButton.WithUrl("🌐 Web Sitesi", "https://ulasim.sakarya.bel.tr") }
        });

        if (updateMessageId.HasValue)
        {
            await _botClient.EditMessageText(chatId, updateMessageId.Value, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
        }
        else
        {
            await _botClient.SendMessage(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
        }
    }

    private async Task ShowLineCategoriesAsync(long chatId, CancellationToken token, int? updateMessageId = null)
    {
        var text = "🔍 **Hangi tür araçları listelemek istersiniz?**";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new [] { InlineKeyboardButton.WithCallbackData("🔴 Belediye Otobüsü", "cat_belediye_0") },
            new [] { InlineKeyboardButton.WithCallbackData("🔵 Özel Halk Otobüsü", "cat_ozel_0") },
            new [] { InlineKeyboardButton.WithCallbackData("🟣 Minibüs", "cat_minibus_0") },
            new [] { InlineKeyboardButton.WithCallbackData("🟡 Taksi Dolmuş", "cat_taksi_0") },
            new [] { InlineKeyboardButton.WithCallbackData("🔙 Ana Menü", "main_menu") }
        });

        if (updateMessageId.HasValue)
            await _botClient.EditMessageText(chatId, updateMessageId.Value, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
        else
            await _botClient.SendMessage(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
    }

    public async Task HandleCallbackQueryAsync(CallbackQuery callback, CancellationToken token)
    {
        var data = callback.Data;
        var chatId = callback.Message?.Chat.Id ?? 0;
        var messageId = callback.Message?.MessageId ?? 0;

        if (string.IsNullOrEmpty(data) || chatId == 0) return;

        try
        {
            if (data == "main_menu")
            {
                await ShowMainMenuAsync(chatId, token, messageId);
            }
            else if (data == "cmd_hat_secim")
            {
                await ShowLineCategoriesAsync(chatId, token, messageId);
            }
            else if (data.StartsWith("cat_"))
            {
                // cat_belediye_0 (page 0)
                var parts = data.Split('_');
                var category = parts[1];
                var page = int.Parse(parts[2]);
                await ListLinesAsync(chatId, category, page, token, messageId);
            }
            else if (data.StartsWith("line_"))
            {
                // line_137_select
                var parts = data.Split('_');
                var lineId = int.Parse(parts[1]);
                var action = parts[2];

                if (action == "select") await ShowLineDashboardAsync(chatId, lineId, token, messageId);
                else if (action == "schedule") await ShowLineScheduleAsync(chatId, lineId, token, messageId);
                else if (action == "route") await ShowLineRouteInfoAsync(chatId, lineId, token, messageId);
                else if (action == "fare") await ShowLineFareAsync(chatId, lineId, token, messageId);
                else if (action == "announcement") await ShowLineAnnouncementsAsync(chatId, lineId, token, messageId);
                else if (action == "nextbus") await ShowNextBusAsync(chatId, lineId, token, messageId);
            }

            // Acknowledge the callback to stop the loading animation
            await _botClient.AnswerCallbackQuery(callback.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling callback: {Data}", data);
            await _botClient.AnswerCallbackQuery(callback.Id, "Bir hata oluştu.", true);
        }
    }

    private static string GetTurkishStatus(string status)
    {
        return status?.ToUpperInvariant() switch
        {
            "CRUISE" => "Seyir Halinde",
            "AT_STOP" => "Durakta",
            "APPROACH" => "Durağa Yaklaşıyor",
            "DEPARTURE" => "Duraktan Ayrıldı",
            "IDLE" => "Beklemede",
            "OUT_OF_SERVICE" => "Servis Dışı",
            "NOT_IN_SERVICE" => "Servis Dışı",
            _ => status ?? "Bilinmiyor"
        };
    }

    private async Task ShowNextBusAsync(long chatId, int lineId, CancellationToken token, int messageId)
    {
        var sb = new StringBuilder();
        
        // 1. Check Real-Time Vehicles
        try
        {
            var vehicles = await _vehicleService.GetVehicleLocationsAsync(lineId);
            if (vehicles.Count > 0)
            {
                sb.AppendLine($"📡 **Canlı Araç Takibi ({vehicles.Count} araç)**");
                foreach (var v in vehicles)
                {
                    var turkishStatus = GetTurkishStatus(v.Status);
                    string statusIcon = v.Status?.ToUpperInvariant() switch
                    {
                        "AT_STOP" => "🛑",
                        "APPROACH" => "🔜",
                        "DEPARTURE" => "🟢",
                        "IDLE" => "⏸️",
                        _ => "🚍"
                    };
                    
                    var currentStop = !string.IsNullOrEmpty(v.CurrentStopName) ? v.CurrentStopName : null;
                    var nextStop = !string.IsNullOrEmpty(v.NextStopName) ? v.NextStopName : null;
                    var dist = v.DistNextStopMeter.HasValue ? $"({v.DistNextStopMeter.Value:F0}m)" : "";
                    
                    sb.AppendLine($"{statusIcon} **Araç {v.BusNumber}** — {turkishStatus}");
                    sb.AppendLine($"   🏎️ Hız: {v.Speed:F0} km/h");
                    if (currentStop != null)
                        sb.AppendLine($"   📍 Bulunduğu Durak: {currentStop}");
                    if (nextStop != null)
                        sb.AppendLine($"   ➡️ Sonraki Durak: {nextStop} {dist}");
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("⚠️ Şu an aktif araç sinyali alınamıyor.");
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching vehicle data.");
        }

        // 2. Check Schedule (Fallback / Plan)
        var schedule = await FindScheduleAnywhere(lineId);
        
        if (schedule != null)
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"));
            // Determine day type
            string dayType = "Hafta İçi";
            if (now.DayOfWeek == DayOfWeek.Saturday) dayType = "Cumartesi";
            if (now.DayOfWeek == DayOfWeek.Sunday) dayType = "Pazar";

            // Fuzzy match keys
            var relevantKey = schedule.DayTimes.Keys.FirstOrDefault(k => k.Contains(dayType, StringComparison.OrdinalIgnoreCase)) 
                              ?? schedule.DayTimes.Keys.FirstOrDefault(); 

            if (relevantKey != null && schedule.DayTimes.TryGetValue(relevantKey, out var times))
            {
                var upcoming = new List<(TimeSpan Time, TimeSpan Diff)>();
                foreach (var tStr in times)
                {
                    if (TimeSpan.TryParse(tStr, out var ts))
                    {
                        var diff = ts - now.TimeOfDay;
                        if (diff.TotalMinutes > -5) // Show if just missed or upcoming
                        {
                            upcoming.Add((ts, diff));
                        }
                    }
                }
                
                upcoming = upcoming.OrderBy(x => x.Diff).Take(3).ToList();

                if (upcoming.Count > 0)
                {
                    sb.AppendLine($"⏳ **Tarifeye Göre Sıradaki Seferler ({dayType}):**");
                    foreach (var (time, diff) in upcoming)
                    {
                        if (diff.TotalMinutes < 0)
                            sb.AppendLine($"🔴 {time:hh\\:mm} (Kaçtı)");
                        else if (diff.TotalMinutes < 60)
                             sb.AppendLine($"🟢 **{time:hh\\:mm}** ({Math.Ceiling(diff.TotalMinutes)} dk kaldı)");
                        else
                             sb.AppendLine($"⚪ {time:hh\\:mm}");
                    }
                }
                else
                {
                    sb.AppendLine("😴 Bugün için başka sefer görünmüyor.");
                }
            }
            else
            {
                sb.AppendLine("⚠️ Bugün için tarife kaydı yok.");
            }
        }
        else
        {
             sb.AppendLine("⚠️ Tarife verisi bulunamadı.");
        }
        
        var keyboard = new InlineKeyboardMarkup(new[]{ InlineKeyboardButton.WithCallbackData("🔙 Geri", $"line_{lineId}_select") });
        await _botClient.EditMessageText(chatId, messageId, sb.ToString(), parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
    }

    private async Task ShowLineRouteInfoAsync(long chatId, int lineId, CancellationToken token, int messageId)
    {
        var route = await StorageHelper.ReadRouteDataAsync(lineId.ToString());
        var text = "";
        
        if (route != null && route.Routes.Any())
        {
            var sb = new StringBuilder();
            sb.AppendLine($"🗺️ **Güzergah Bilgisi:**");
            foreach (var r in route.Routes)
            {
                sb.AppendLine($"\n📍 *{r.RouteName}*");
                sb.AppendLine($"Başlangıç: {r.StartLocation}");
                sb.AppendLine($"Bitiş: {r.EndLocation}");
                sb.AppendLine($"Durak Sayısı: {r.BusStops.Count}");
            }
            sb.AppendLine($"\n🔗 [Haritada Göster](https://ulasim.sakarya.bel.tr/ulasim/hat-detay/{lineId})");
            text = sb.ToString();
        }
        else
        {
             text = "⚠️ Güzergah verisi henüz yüklenmemiş.";
        }

        var keyboard = new InlineKeyboardMarkup(new[]{ InlineKeyboardButton.WithCallbackData("🔙 Geri", $"line_{lineId}_select") });
        await _botClient.EditMessageText(chatId, messageId, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
    }

    private async Task ListLinesAsync(long chatId, string category, int page, CancellationToken token, int messageId)
    {
        await EnsureLinesLoadedAsync();

        List<LineItem> filteredLines = category switch
        {
            "belediye" => _cachedLines.Where(x => x.Type == "Belediye").ToList(),
            "ozel" => _cachedLines.Where(x => x.Type == "Özel Halk").ToList(),
            "minibus" => _cachedLines.Where(x => x.Type == "Minibüs").ToList(),
            "taksi" => _cachedLines.Where(x => x.Type == "Taksi-Dolmuş").ToList(),
            _ => new List<LineItem>()
        };

        const int PageSize = 10;
        int totalPages = (int)Math.Ceiling((double)filteredLines.Count / PageSize);
        var pageLines = filteredLines.Skip(page * PageSize).Take(PageSize).ToList();

        var buttons = new List<List<InlineKeyboardButton>>();
        
        foreach (var line in pageLines)
        {
            // "24K - KAMPUS"
            buttons.Add(new List<InlineKeyboardButton> 
            { 
                InlineKeyboardButton.WithCallbackData($"🚍 {line.Display}", $"line_{line.Id}_select") 
            });
        }

        // Pagination buttons
        var navRow = new List<InlineKeyboardButton>();
        if (page > 0)
            navRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Önceki", $"cat_{category}_{page - 1}"));
        
        navRow.Add(InlineKeyboardButton.WithCallbackData($"Sayfa {page + 1}/{totalPages}", "ignore"));

        if (page < totalPages - 1)
            navRow.Add(InlineKeyboardButton.WithCallbackData("Sonraki ➡️", $"cat_{category}_{page + 1}"));

        buttons.Add(navRow);
        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🔙 Geri Dön", "cmd_hat_secim") });

        var keyboard = new InlineKeyboardMarkup(buttons);
        var title = category.ToUpper() switch { "BELEDIYE" => "🔴 Belediye", "OZEL" => "🔵 Özel Halk", "MINIBUS" => "🟣 Minibüs", "TAKSI" => "� Taksi Dolmuş", _ => category };
        
        await _botClient.EditMessageText(chatId, messageId, $"📄 **{title} Hatları** (Sayfa {page+1})", parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
    }

    private async Task ShowLineDashboardAsync(long chatId, int lineId, CancellationToken token, int messageId)
    {
        await EnsureLinesLoadedAsync();
        var line = _cachedLines.FirstOrDefault(x => x.Id == lineId);
        if (line == null) { await _botClient.AnswerCallbackQuery("Hat bulunamadı.", chatId.ToString()); return; }

        var text = $"🚍 **{line.Display}**\n\nBu hat ile ilgili ne yapmak istersiniz?";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new [] 
            { 
                InlineKeyboardButton.WithCallbackData("🕒 Sefer Saatleri", $"line_{lineId}_schedule"),
                InlineKeyboardButton.WithCallbackData("⏳ Sıradaki Sefer", $"line_{lineId}_nextbus") 
            },
            new [] 
            { 
                InlineKeyboardButton.WithCallbackData("💰 Fiyat Tarifesi", $"line_{lineId}_fare"), 
                InlineKeyboardButton.WithCallbackData("🗺️ Güzergah", $"line_{lineId}_route") 
            },
             new [] 
            { 
                InlineKeyboardButton.WithCallbackData("� Duyurular", $"line_{lineId}_announcement"), 
            },
            new [] { InlineKeyboardButton.WithCallbackData("🔙 Listeye Dön", "cmd_hat_secim") }
        });

        await _botClient.EditMessageText(chatId, messageId, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
    }

    // --- SUB FEATURES ---

    private async Task ShowLineScheduleAsync(long chatId, int lineId, CancellationToken token, int messageId)
    {
        var schedule = await StorageHelper.ReadScheduleAsync(lineId.ToString(), "belediye-otobusleri"); // Todo: dynamic subfolder?
        // Actually we don't know the exact subfolder easily without trying. 
        // But we can fetch it live? No, user wanted from cache.
        // Let's create a helper to find schedule by searching subfolders.
        
        schedule ??= await FindScheduleAnywhere(lineId);

        var text = "";
        if (schedule != null && schedule.DayTimes.Any())
        {
            var sb = new StringBuilder();
            sb.AppendLine($"🕒 **Sefer Saatleri: {schedule.LineName}**");
            foreach (var kvp in schedule.DayTimes)
            {
                sb.AppendLine($"\n📅 *{kvp.Key}*");
                sb.AppendLine(string.Join(", ", kvp.Value));
            }
            text = sb.ToString();
        }
        else
        {
            text = "⚠️ Bu hat için sefer saati verisi bulunamadı veya sadece anlık takip yapılıyor olabilir.";
        }

        var keyboard = new InlineKeyboardMarkup(new[]{ InlineKeyboardButton.WithCallbackData("🔙 Geri", $"line_{lineId}_select") });
        await _botClient.EditMessageText(chatId, messageId, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
    }
    
    // --- HELPERS ---

    private async Task EnsureLinesLoadedAsync()
    {
        if (_cachedLines.Count > 0 && (DateTime.Now - _lastCacheTime).TotalMinutes < 30) return;

        _cachedLines.Clear();
        var data = await StorageHelper.ReadBusLinesAsync();
        
        foreach (var l in data.belediye) AddLine(l, "Belediye");
        foreach (var l in data.ozel_halk) AddLine(l, "Özel Halk");
        foreach (var l in data.taksi_dolmus) AddLine(l, "Taksi-Dolmuş");
        foreach (var l in data.minibus) AddLine(l, "Minibüs");

        _lastCacheTime = DateTime.Now;
    }

    private void AddLine(string entry, string type)
    {
        // Entry format: "24K - KAMPUS|URL"
        var parts = entry.Split('|');
        var namePart = parts[0];
        // Parse ID from somewhere? 
        // Wait, the "lines" list strings don't have IDs directly visible in that format "Number - Name".
        // BUT, we stored them as "FullText|Url". URL contains slug "num-name-ID".
        // We need to extract ID from slug.
        
        var url = parts.Length > 1 ? parts[1] : "";
        int id = 0;

        if (!string.IsNullOrEmpty(url))
        {
            var slug = url.Split('/').Last();
            var idPart = slug.Split('-').Last();
            int.TryParse(idPart, out id);
        }

        if (id != 0)
        {
            _cachedLines.Add(new LineItem { Id = id, Display = namePart, Type = type });
        }
    }

    private async Task SearchLineAsync(long chatId, string query, CancellationToken token)
    {
        await EnsureLinesLoadedAsync();
        var match = _cachedLines.FirstOrDefault(x => x.Display.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        
        if (match != null)
        {
            await _botClient.SendMessage(chatId, $"✨ Eşleşen hat bulundu:", cancellationToken: token);
            await ShowLineDashboardAsync(chatId, match.Id, token, (await _botClient.SendMessage(chatId, "...", cancellationToken: token)).MessageId);
        }
        else
        {
            await _botClient.SendMessage(chatId, "❌ Hat bulunamadı. Lütfen tam numarasını yazın (örn: 24K) veya menüden seçin.", cancellationToken: token);
        }
    }

    private async Task<BusSchedule?> FindScheduleAnywhere(int lineId)
    {
        // Try known folders
        var folders = new[] { "belediye-otobusleri", "ozel-halk-otobusleri" };
        foreach (var f in folders)
        {
            // We need the line Name to find the file... StorageHelper takes name.
            // But we only have ID here.
            // This is a design flaw in my storage strategy (Keying by Name instead of ID).
            // Workaround: Find name from cached list then load.
            
            var line = _cachedLines.FirstOrDefault(x => x.Id == lineId);
            if (line == null) continue;

            // The file is named like "24K_-_Name.json"
            // Let's try to match the file in directory?
            // Or change ReadScheduleAsync to iterate?
            
            // For now, let's look for a file that contains the ID inside content? No, slow.
            // Best bet: The line.Display acts as name.
            
            var schedule = await StorageHelper.ReadScheduleAsync(line.Display, f);
            if (schedule != null) return schedule;
        }
        return null;
    }

    // Placeholders for unimplemented
    private async Task ShowLineFareAsync(long chatId, int lineId, CancellationToken token, int messageId)
    {
        string text;
        try
        {
            string? json = null;
            var cacheDir = Path.Combine(StorageHelper.GetDataPath(), "fare_cache");
            var cachePath = Path.Combine(cacheDir, $"{lineId}.json");

            // Try reading from cache first (valid for 24 hours)
            if (File.Exists(cachePath))
            {
                var age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalHours;
                if (age < 24)
                {
                    json = await File.ReadAllTextAsync(cachePath, token);
                }
            }

            // If no cache, fetch from API and save
            if (string.IsNullOrEmpty(json))
            {
                var client = _httpClientFactory.CreateClient();
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://sbbpublicapi.sakarya.bel.tr/api/v1/Ulasim/line-fare/{lineId}?busType=3869");
                request.Headers.Add("Origin", "https://ulasim.sakarya.bel.tr");
                request.Headers.Add("Referer", "https://ulasim.sakarya.bel.tr");
                request.Headers.Add("User-Agent", "Mozilla/5.0");

                var response = await client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    text = "⚠️ Tarife bilgisi şu an alınamıyor.";
                    var keyboard2 = new InlineKeyboardMarkup(new[]{ InlineKeyboardButton.WithCallbackData("🔙 Geri", $"line_{lineId}_select") });
                    await _botClient.EditMessageText(chatId, messageId, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard2, cancellationToken: token);
                    return;
                }

                json = await response.Content.ReadAsStringAsync(token);

                // Save to cache
                try
                {
                    if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                    await File.WriteAllTextAsync(cachePath, json, token);
                }
                catch { /* Cache write failure is non-critical */ }
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sb = new StringBuilder();
            sb.AppendLine("💰 **Fiyat Tarifesi**");
            sb.AppendLine();

            if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    var groupName = group.GetProperty("name").GetString();
                    sb.AppendLine($"📍 *{groupName}*");

                    if (group.TryGetProperty("routes", out var routes))
                    {
                        foreach (var route in routes.EnumerateArray())
                        {
                            var routeName = route.GetProperty("routeName").GetString();
                            sb.AppendLine($"  🔹 {routeName}");

                            if (route.TryGetProperty("tariffs", out var tariffs))
                            {
                                foreach (var tariff in tariffs.EnumerateArray())
                                {
                                    var finalFare = tariff.GetProperty("finalFare").GetDecimal();
                                    var typeId = tariff.GetProperty("lineFareTypeId").GetInt32();
                                    var typeName = "Diğer";
                                    if (root.TryGetProperty("tariffList", out var tList) && tList.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var t in tList.EnumerateArray())
                                        {
                                            if (t.GetProperty("lineFareTypeId").GetInt32() == typeId)
                                            {
                                                typeName = t.GetProperty("typeName").GetString() ?? "Diğer";
                                                break;
                                            }
                                        }
                                    }
                                    sb.AppendLine($"      🔸 {typeName}: *{finalFare:F2} TL*");
                                }
                            }
                        }
                    }
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("Bu hat için tarife bilgisi bulunamadı.");
            }
            text = sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching fare for line {LineId}", lineId);
            text = "⚠️ Tarife bilgisi alınırken hata oluştu.";
        }

        var keyboard = new InlineKeyboardMarkup(new[]{ InlineKeyboardButton.WithCallbackData("🔙 Geri", $"line_{lineId}_select") });
        await _botClient.EditMessageText(chatId, messageId, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
    }

    private async Task ShowLineAnnouncementsAsync(long chatId, int lineId, CancellationToken token, int messageId)
    {
        string text;
        try
        {
            var dataPath = Path.Combine(StorageHelper.GetDataPath(), "announcement_data.json");
            if (!File.Exists(dataPath))
            {
                text = "📢 **Duyurular**\n\nDuyuru verisi henüz yüklenmedi.";
            }
            else
            {
                var json = await File.ReadAllTextAsync(dataPath, token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Find line display name to match announcements
                await EnsureLinesLoadedAsync();
                var line = _cachedLines.FirstOrDefault(x => x.Id == lineId);

                var sb = new StringBuilder();
                sb.AppendLine("📢 **Duyurular**");
                sb.AppendLine();

                bool found = false;

                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        // Check if this announcement belongs to this line
                        int? announcementLineId = null;
                        if (item.TryGetProperty("lineId", out var lidEl) && lidEl.ValueKind == JsonValueKind.Number)
                            announcementLineId = lidEl.GetInt32();

                        var lineNumber = item.TryGetProperty("lineNumber", out var lnEl) ? lnEl.GetString() ?? "" : "";
                        var lineName = item.TryGetProperty("lineName", out var lnmEl) ? lnmEl.GetString() ?? "" : "";

                        // Match by lineId or by line display name
                        bool matches = announcementLineId == lineId;
                        if (!matches && line != null)
                        {
                            matches = line.Display.Contains(lineNumber, StringComparison.OrdinalIgnoreCase) 
                                   || line.Display.Contains(lineName, StringComparison.OrdinalIgnoreCase);
                        }

                        if (!matches) continue;

                        found = true;
                        var title = item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                        var content = item.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
                        var category = item.TryGetProperty("categoryName", out var catEl) ? catEl.GetString() ?? "" : "";
                        var startDate = item.TryGetProperty("startDate", out var sdEl) ? sdEl.GetString() : null;
                        var endDate = item.TryGetProperty("endDate", out var edEl) ? edEl.GetString() : null;

                        sb.AppendLine($"🔔 *{title}*");
                        if (!string.IsNullOrEmpty(category))
                            sb.AppendLine($"🏷 _{category}_");

                        // Clean HTML from content
                        var cleanContent = System.Text.RegularExpressions.Regex.Replace(content, @"<[^>]+>", "");
                        cleanContent = System.Net.WebUtility.HtmlDecode(cleanContent).Trim();
                        if (cleanContent.Length > 300)
                            cleanContent = cleanContent[..300] + "...";
                        if (!string.IsNullOrEmpty(cleanContent))
                            sb.AppendLine(cleanContent);

                        // Date range
                        var turkey = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
                        if (!string.IsNullOrEmpty(startDate) && DateTime.TryParse(startDate, null,
                                System.Globalization.DateTimeStyles.RoundtripKind, out var start))
                        {
                            var startLocal = TimeZoneInfo.ConvertTimeFromUtc(start, turkey);
                            sb.Append($"\n📅 _{startLocal:dd.MM.yyyy HH:mm}");
                            if (!string.IsNullOrEmpty(endDate) && DateTime.TryParse(endDate, null,
                                    System.Globalization.DateTimeStyles.RoundtripKind, out var end))
                            {
                                var endLocal = TimeZoneInfo.ConvertTimeFromUtc(end, turkey);
                                sb.Append($" — {endLocal:dd.MM.yyyy HH:mm}");
                            }
                            sb.AppendLine("_");
                        }
                        sb.AppendLine();
                    }
                }

                if (!found)
                {
                    sb.AppendLine("Bu hat için aktif bir duyuru bulunmuyor.");
                }

                text = sb.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading announcements for line {LineId}", lineId);
            text = "⚠️ Duyurular okunurken hata oluştu.";
        }

        var keyboard = new InlineKeyboardMarkup(new[]{ InlineKeyboardButton.WithCallbackData("🔙 Geri", $"line_{lineId}_select") });
        await _botClient.EditMessageText(chatId, messageId, text, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: token);
    }
}

public class LineItem
{
    public int Id { get; set; }
    public string Display { get; set; } = "";
    public string Type { get; set; } = "";
}
