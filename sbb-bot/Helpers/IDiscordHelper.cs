using Discord;

namespace SbbBot.Helpers;

public interface IDiscordHelper
{
    Task StartAsync();
    Task StopAsync();
    Task CheckHealthAsync();
    Task SendEmbedAsync(string botName, string channelName, Embed embed);
    Task SendEmbedWithButtonAsync(string botName, string channelName, Embed embed, string buttonLabel, string buttonUrl);
}
