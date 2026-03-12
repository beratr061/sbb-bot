namespace SbbBot.Models;

public class DiscordBotConfig
{
    public string Name { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public List<DiscordChannelConfig> Channels { get; set; } = new();
}
