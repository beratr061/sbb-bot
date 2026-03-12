namespace SbbBot.Models;

public class DiscordOptions
{
    public bool TestMode { get; set; }
    public List<DiscordBotConfig> Bots { get; set; } = new();
}
