namespace SbbBot;

public class BotConfig
{
    public TelegramConfig Telegram { get; set; } = new();
    public IntervalsConfig Intervals { get; set; } = new();
}

public class TelegramConfig
{
    public string Token { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
}

public class IntervalsConfig
{
    public int NewsMinutes { get; set; }
    public int DocumentsMinutes { get; set; }
    public int BusLinesMinutes { get; set; }
    public int MeetingMinutes { get; set; }
    public int AnnouncementMinutes { get; set; }
    public int UkomeMinutes { get; set; }
    public int FareMinutes { get; set; }
    public int RouteMinutes { get; set; }
    public int NeonPingMinutes { get; set; }
}
