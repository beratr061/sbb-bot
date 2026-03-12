using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SbbBot.Models;

namespace SbbBot.Helpers;

public class DiscordHelper : IDiscordHelper
{
    private readonly Dictionary<string, DiscordSocketClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordHelper> _logger;

    public DiscordHelper(IOptions<DiscordOptions> options, ILogger<DiscordHelper> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        foreach (var botConfig in _options.Bots)
        {
            if (string.IsNullOrWhiteSpace(botConfig.Token))
            {
                _logger.LogWarning("Discord bot '{BotName}' has no token configured. Skipping.", botConfig.Name);
                continue;
            }

            try
            {
                var client = new DiscordSocketClient(new DiscordSocketConfig
                {
                    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages,
                    LogLevel = LogSeverity.Info
                });

                client.Log += msg =>
                {
                    var level = msg.Severity switch
                    {
                        LogSeverity.Critical => LogLevel.Critical,
                        LogSeverity.Error    => LogLevel.Error,
                        LogSeverity.Warning  => LogLevel.Warning,
                        LogSeverity.Info     => LogLevel.Information,
                        LogSeverity.Verbose  => LogLevel.Debug,
                        LogSeverity.Debug    => LogLevel.Trace,
                        _                    => LogLevel.Information
                    };
                    _logger.Log(level, msg.Exception, "[Discord:{BotName}] {Message}", botConfig.Name, msg.Message);
                    return Task.CompletedTask;
                };

                var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Ready += () =>
                {
                    readyTcs.TrySetResult(true);
                    return Task.CompletedTask;
                };

                client.Disconnected += ex =>
                {
                    _logger.LogWarning(ex, "Discord bot '{BotName}' disconnected.", botConfig.Name);
                    return Task.CompletedTask;
                };

                await client.LoginAsync(TokenType.Bot, botConfig.Token);
                await client.StartAsync();

                // Wait for Ready event with a timeout
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
                var completedTask = await Task.WhenAny(readyTcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("Discord bot '{BotName}' did not become ready within 30 seconds.", botConfig.Name);
                }
                else
                {
                    _logger.LogInformation("Discord bot '{BotName}' is ready.", botConfig.Name);
                }

                _clients[botConfig.Name] = client;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Discord bot '{BotName}'. It will be disabled.", botConfig.Name);
            }
        }

        if (_options.TestMode)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    _logger.LogInformation("[Discord] TestMode aktif, örnek embed'ler gönderiliyor...");
                    
                    if (_clients.ContainsKey("SAKUS"))
                    {
                        await SendEmbedWithButtonAsync("SAKUS", "sakarya-ulasim", 
                            DiscordEmbedBuilder.BusLineAdded("99X", "TEST HATTI", "Belediye Otobüsü"), "🚌 Hat Sayfası", "https://ulasim.sakarya.bel.tr");
                    }

                    if (_clients.ContainsKey("BasinDairesi"))
                    {
                        var now = DateTime.UtcNow;
                        await SendEmbedWithButtonAsync("BasinDairesi", "ukome-kararlari", 
                            DiscordEmbedBuilder.UkomeDecision("ÖRNEK UKOME KARARI", "https://ulasim.sakarya.bel.tr/ukome", now), "📋 Karara Git", "https://ulasim.sakarya.bel.tr/ukome");
                        
                        await SendEmbedWithButtonAsync("BasinDairesi", "acik-veri-portali", 
                            DiscordEmbedBuilder.OpenDataUpdated("ÖRNEK VERİ SETİ", "https://veri.sakarya.bel.tr", true), "📊 Veri Setine Git", "https://veri.sakarya.bel.tr");
                        
                        await SendEmbedWithButtonAsync("BasinDairesi", "haberler", 
                            DiscordEmbedBuilder.NewsPublished("ÖRNEK HABER BAŞLIĞI", "https://ulasim.sakarya.bel.tr", now), "📰 Habere Git", "https://ulasim.sakarya.bel.tr");
                        
                        await SendEmbedWithButtonAsync("BasinDairesi", "butce-ve-stratejik-yonetim", 
                            DiscordEmbedBuilder.DocumentPublished("ÖRNEK DÖKÜMAN", "https://ulasim.sakarya.bel.tr", now), "📄 Dökümana Git", "https://ulasim.sakarya.bel.tr");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TestMode error.");
                }
            });
        }
    }

    public async Task StopAsync()
    {
        foreach (var (botName, client) in _clients)
        {
            try
            {
                await client.LogoutAsync();
                await client.StopAsync();
                _logger.LogInformation("Discord bot '{BotName}' stopped.", botName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Discord bot '{BotName}'.", botName);
            }
        }

        _clients.Clear();
    }

    public async Task SendEmbedAsync(string botName, string channelName, Embed embed)
    {
        // Find the client
        if (!_clients.TryGetValue(botName, out var client))
        {
            _logger.LogWarning("Discord bot '{BotName}' not found or not started. Cannot send embed.", botName);
            return;
        }

        // Find channel config
        var botConfig = _options.Bots.FirstOrDefault(b => 
            string.Equals(b.Name, botName, StringComparison.OrdinalIgnoreCase));

        if (botConfig is null)
        {
            _logger.LogWarning("Discord bot config for '{BotName}' not found.", botName);
            return;
        }

        var channelConfig = botConfig.Channels.FirstOrDefault(c => 
            string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        if (channelConfig is null)
        {
            _logger.LogWarning("Channel '{ChannelName}' not found in bot '{BotName}' config.", channelName, botName);
            return;
        }

        if (channelConfig.ChannelId == 0)
        {
            _logger.LogWarning("Channel '{ChannelName}' in bot '{BotName}' has no ChannelId configured. Skipping.", channelName, botName);
            return;
        }

        try
        {
            var channel = client.GetChannel(channelConfig.ChannelId);

            if (channel is not IMessageChannel messageChannel)
            {
                _logger.LogWarning(
                    "Channel '{ChannelName}' (ID: {ChannelId}) in bot '{BotName}' is not a message channel or was not found.",
                    channelName, channelConfig.ChannelId, botName);
                return;
            }

            await messageChannel.SendMessageAsync(embed: embed);
            _logger.LogInformation(
                "Embed sent to channel '{ChannelName}' (ID: {ChannelId}) via bot '{BotName}'.",
                channelName, channelConfig.ChannelId, botName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send embed to channel '{ChannelName}' (ID: {ChannelId}) via bot '{BotName}'.",
                channelName, channelConfig.ChannelId, botName);
        }
    }

    public async Task SendEmbedWithButtonAsync(string botName, string channelName, Embed embed, string buttonLabel, string buttonUrl)
    {
        // Find the client
        if (!_clients.TryGetValue(botName, out var client))
        {
            _logger.LogWarning("Discord bot '{BotName}' not found or not started. Cannot send embed.", botName);
            return;
        }

        // Find channel config
        var botConfig = _options.Bots.FirstOrDefault(b => 
            string.Equals(b.Name, botName, StringComparison.OrdinalIgnoreCase));

        if (botConfig is null)
        {
            _logger.LogWarning("Discord bot config for '{BotName}' not found.", botName);
            return;
        }

        var channelConfig = botConfig.Channels.FirstOrDefault(c => 
            string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        if (channelConfig is null)
        {
            _logger.LogWarning("Channel '{ChannelName}' not found in bot '{BotName}' config.", channelName, botName);
            return;
        }

        if (channelConfig.ChannelId == 0)
        {
            _logger.LogWarning("Channel '{ChannelName}' in bot '{BotName}' has no ChannelId configured. Skipping.", channelName, botName);
            return;
        }

        try
        {
            var channel = client.GetChannel(channelConfig.ChannelId);

            if (channel is not IMessageChannel messageChannel)
            {
                _logger.LogWarning(
                    "Channel '{ChannelName}' (ID: {ChannelId}) in bot '{BotName}' is not a message channel or was not found.",
                    channelName, channelConfig.ChannelId, botName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(buttonUrl) && buttonUrl.StartsWith("https://"))
            {
                var component = new ComponentBuilder()
                    .WithButton(buttonLabel, style: ButtonStyle.Link, url: buttonUrl)
                    .Build();
                await messageChannel.SendMessageAsync(embed: embed, components: component);
            }
            else
            {
                await messageChannel.SendMessageAsync(embed: embed);
            }
            _logger.LogInformation(
                "Embed sent to channel '{ChannelName}' (ID: {ChannelId}) via bot '{BotName}'.",
                channelName, channelConfig.ChannelId, botName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send embed to channel '{ChannelName}' (ID: {ChannelId}) via bot '{BotName}'.",
                channelName, channelConfig.ChannelId, botName);
        }
    }

    public Task CheckHealthAsync()
    {
        foreach (var botConfig in _options.Bots)
        {
            if (!_clients.TryGetValue(botConfig.Name, out var client))
            {
                _logger.LogWarning("[Discord][{botName}] Durum: Çalışmıyor/Açılmadı", botConfig.Name);
                continue;
            }

            var activeChannels = new List<string>();
            foreach (var ch in botConfig.Channels)
            {
                if (ch.ChannelId == 0) continue;

                var channel = client.GetChannel(ch.ChannelId);
                if (channel is IGuildChannel guildChannel)
                {
                    activeChannels.Add(guildChannel.Name);
                }
                else
                {
                    activeChannels.Add(ch.ChannelId.ToString());
                }
            }

            var kanallarTxt = activeChannels.Count > 0 ? string.Join(", ", activeChannels) : "Yok";
            
            _logger.LogInformation("[Discord][{botName}] Durum: {ConnectionState} | Aktif Kanallar: {Channels}", 
                botConfig.Name, client.ConnectionState, kanallarTxt);
        }

        return Task.CompletedTask;
    }
}
