using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using SbbBot;

namespace SbbBot.Helpers;

public class TelegramHelper
{
    private readonly TelegramBotClient _botClient;
    private readonly string _chatId;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly ILogger<TelegramHelper> _logger;

    public TelegramHelper(IOptions<BotConfig> config, ILogger<TelegramHelper> logger)
    {
        _logger = logger;
        var token = config.Value.Telegram.Token;
        _chatId = config.Value.Telegram.ChatId;
        try
        {
            _botClient = new TelegramBotClient(token);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, $"Invalid Telegram Bot Token provided: '{token}'. Please check appsettings.Development.json.");
            throw; 
        }

        // Polly retry policy: 3 retries with 2 seconds wait
        _retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(2),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning($"Telegram message send failed. Retry {retryCount}. Error: {exception.Message}");
                });
    }

    public async Task SendMessageAsync(string message)
    {
        if (string.IsNullOrEmpty(_chatId))
        {
            _logger.LogWarning("Telegram ChatId is not configured. Message not sent.");
            return;
        }

        await _retryPolicy.ExecuteAsync(async () =>
        {
            await _botClient.SendMessage(
                chatId: _chatId,
                text: message,
                parseMode: ParseMode.Markdown
            );
        });
    }
}
