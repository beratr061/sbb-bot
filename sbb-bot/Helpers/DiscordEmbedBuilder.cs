using Discord;

namespace SbbBot.Helpers;

public static class DiscordEmbedBuilder
{
    private const int FieldMaxLength = 1024;
    private const int DescriptionMaxLength = 2048;

    // ───────────────────── SAKUS BOTU EMBEDLERİ ─────────────────────

    /// <summary>
    /// Yeni hat açıldığında gönderilecek embed.
    /// </summary>
    public static Embed BusLineAdded(string lineCode, string lineName, string busType)
    {
        return new EmbedBuilder()
            .WithColor(Color.Green)
            .WithTitle("🆕 Yeni Hat Açıldı")
            .WithDescription($"**{lineName}** hattı sisteme eklendi.")
            .AddField("Hat Kodu", lineCode ?? "-", inline: false)
            .AddField("Hat Adı", lineName ?? "-", inline: false)
            .AddField("Tür", busType ?? "-", inline: false)
            .AddField("\u200b", "\u200b", inline: false)
            .WithDefaultFooter()
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    /// <summary>
    /// Hat kaldırıldığında gönderilecek embed.
    /// </summary>
    public static Embed BusLineRemoved(string lineCode, string lineName)
    {
        return new EmbedBuilder()
            .WithColor(Color.Red)
            .WithTitle("❌ Hat Kaldırıldı")
            .WithDescription($"**{lineName}** hattı sistemden kaldırıldı.")
            .AddField("Hat Kodu", lineCode ?? "-", inline: false)
            .AddField("Hat Adı", lineName ?? "-", inline: false)
            .AddField("\u200b", "\u200b", inline: false)
            .WithDefaultFooter()
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    /// <summary>
    /// Güzergah değişikliği olduğunda gönderilecek embed.
    /// Durak listeleri 1024 karakter limitine göre kesilir.
    /// </summary>
    public static Embed RouteChanged(
        string lineCode,
        string lineName,
        List<string> addedStops,
        List<string> removedStops)
    {
        var builder = new EmbedBuilder()
            .WithColor(new Color(255, 165, 0))
            .WithTitle("📍 Güzergah Değişikliği")
            .WithDescription($"**{lineName}** hattının güzergahında değişiklik tespit edildi.")
            .AddField("Hat Kodu", lineCode ?? "-", inline: false)
            .AddField("Hat Adı", lineName ?? "-", inline: false);

        var addedText = FormatStopList(addedStops);
        if (!string.IsNullOrEmpty(addedText))
            builder.AddField("Eklenen Duraklar", addedText, inline: false);

        var removedText = FormatStopList(removedStops);
        if (!string.IsNullOrEmpty(removedText))
            builder.AddField("Kaldırılan Duraklar", removedText, inline: false);

        builder.AddField("\u200b", "\u200b", inline: false);

        return builder
            .WithDefaultFooter()
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    /// <summary>
    /// Hat duyurusu yayınlandığında gönderilecek embed.
    /// </summary>
    public static Embed AnnouncementNew(
        string title,
        string content,
        DateTime startDate,
        DateTime endDate)
    {
        var description = TruncateWithEllipsis(content, DescriptionMaxLength);

        return new EmbedBuilder()
            .WithColor(Color.Blue)
            .WithTitle("📢 Hat Duyurusu")
            .WithDescription(description ?? string.Empty)
            .AddField("\u200b", "\u200b", inline: false)
            .WithFooter($"SBB Bot • Geçerlilik: {startDate:dd.MM} - {endDate:dd.MM.yyyy}")
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    /// <summary>
    /// Fiyat tarifesi değiştiğinde gönderilecek embed.
    /// Artış 🔴, düşüş 🟢 ikonu ile gösterilir.
    /// </summary>
    public static Embed FareChanged(
        string lineCode,
        string lineName,
        decimal oldFull,
        decimal newFull,
        decimal oldStudent,
        decimal newStudent,
        decimal oldDiscounted,
        decimal newDiscounted)
    {
        return new EmbedBuilder()
            .WithColor(new Color(255, 215, 0))
            .WithTitle("💰 Fiyat Tarifesi Değişti")
            .WithDescription($"**{lineName}** hattına yeni fiyat tarifesi uygulandı.")
            .AddField("Hat Kodu", lineCode ?? "-", inline: false)
            .AddField("Hat Adı", lineName ?? "-", inline: false)
            .AddField("Tam", FormatFare(oldFull, newFull), inline: false)
            .AddField("Öğrenci", FormatFare(oldStudent, newStudent), inline: false)
            .AddField("İndirimli", FormatFare(oldDiscounted, newDiscounted), inline: false)
            .AddField("\u200b", "\u200b", inline: false)
            .WithDefaultFooter()
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    // ───────────────── BASIN DAİRESİ BOTU EMBEDLERİ ─────────────────

    /// <summary>
    /// Yeni UKOME kararı yayınlandığında gönderilecek embed.
    /// </summary>
    public static Embed UkomeDecision(string title, string url, DateTime date)
    {
        return new EmbedBuilder()
            .WithColor(new Color(128, 0, 128))
            .WithTitle("📋 Yeni UKOME Kararı")
            .WithDescription(title)
            .AddField("Karar Tarihi", date.ToString("dd.MM.yyyy"), inline: false)
            .AddField("\u200b", "\u200b", inline: false)
            .WithDefaultFooter()
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    /// <summary>
    /// Açık veri seti yayınlandığında veya güncellendiğinde gönderilecek embed.
    /// </summary>
    public static Embed OpenDataUpdated(string datasetName, string url, bool isNew)
    {
        var embedTitle = isNew
            ? "📊 Yeni Veri Seti Yayınlandı"
            : "📊 Veri Seti Güncellendi";

        return new EmbedBuilder()
            .WithColor(Color.Teal)
            .WithTitle(embedTitle)
            .WithDescription($"**{datasetName}** veri seti güncellendi.")
            .AddField("Veri Seti", datasetName ?? "-", inline: false)
            .AddField("\u200b", "\u200b", inline: false)
            .WithDefaultFooter()
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    /// <summary>
    /// Yeni haber yayınlandığında gönderilecek embed.
    /// </summary>
    public static Embed NewsPublished(string title, string url, DateTime date)
    {
        return new EmbedBuilder()
            .WithColor(Color.LightGrey)
            .WithTitle("📰 Yeni Haber")
            .WithDescription(title)
            .AddField("Tarih", date.ToString("dd.MM.yyyy"), inline: false)
            .AddField("\u200b", "\u200b", inline: false)
            .WithDefaultFooter()
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    /// <summary>
    /// Yeni döküman yayınlandığında gönderilecek embed.
    /// </summary>
    public static Embed DocumentPublished(string title, string url, DateTime date)
    {
        return new EmbedBuilder()
            .WithColor(new Color(70, 130, 180))
            .WithTitle("📄 Yeni Döküman Yayınlandı")
            .WithDescription(title)
            .AddField("Tarih", date.ToString("dd.MM.yyyy"), inline: false)
            .AddField("\u200b", "\u200b", inline: false)
            .WithDefaultFooter()
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    // ───────────────────── YARDIMCI METODLAR ─────────────────────

    /// <summary>
    /// EmbedBuilder'a varsayılan footer ekler.
    /// </summary>
    private static EmbedBuilder WithDefaultFooter(this EmbedBuilder builder)
    {
        return builder.WithFooter($"SBB Bot • {DateTime.Now:dd.MM.yyyy HH:mm}");
    }

    /// <summary>
    /// Durak listesini numaralı satırlar halinde biçimlendirir.
    /// 1024 karakter limitini aşarsa keser ve kalan sayısını ekler.
    /// </summary>
    private static string FormatStopList(List<string>? stops)
    {
        if (stops is null || stops.Count == 0)
            return "-";

        var lines = new List<string>();
        var currentLength = 0;

        for (var i = 0; i < stops.Count; i++)
        {
            var line = $"{i + 1}. {stops[i] ?? "-"}";
            var suffixTemplate = $"\n... ve {{0}} durak daha";
            var worstCaseSuffix = string.Format(suffixTemplate, stops.Count - (i + 1));

            // +1 for the newline between lines
            var projectedLength = currentLength
                + (lines.Count > 0 ? 1 : 0)
                + line.Length;

            // Check if we'd still have room for the "... ve X durak daha" suffix
            if (projectedLength + worstCaseSuffix.Length > FieldMaxLength && i < stops.Count - 1)
            {
                var remaining = stops.Count - i;
                lines.Add($"... ve {remaining} durak daha");
                break;
            }

            // Check if the line itself would exceed the limit
            if (projectedLength > FieldMaxLength)
            {
                var remaining = stops.Count - i;
                lines.Add($"... ve {remaining} durak daha");
                break;
            }

            lines.Add(line);
            currentLength = projectedLength;
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Fiyat değişimini "eski₺ → yeni₺  🔴/🟢" formatında biçimlendirir.
    /// </summary>
    private static string FormatFare(decimal oldPrice, decimal newPrice)
    {
        var indicator = newPrice > oldPrice ? "🔴" : "🟢";
        return $"{oldPrice}₺ → {newPrice}₺  {indicator}";
    }

    /// <summary>
    /// Metni belirtilen uzunlukta keser ve sonuna "..." ekler.
    /// </summary>
    private static string? TruncateWithEllipsis(string? text, int maxLength)
    {
        if (text is null)
            return null;

        if (text.Length <= maxLength)
            return text;

        return string.Concat(text.AsSpan(0, maxLength - 3), "...");
    }
}
