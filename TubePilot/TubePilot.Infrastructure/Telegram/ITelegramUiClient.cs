using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TubePilot.Infrastructure.Telegram;

internal interface ITelegramUiClient
{
    Task<int> SendMessageAsync(
        long chatId,
        string text,
        ParseMode? parseMode = null,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken ct = default);

    Task EditMessageTextAsync(
        long chatId,
        int messageId,
        string text,
        ParseMode? parseMode = null,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken ct = default);

    Task AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text = null,
        bool showAlert = false,
        CancellationToken ct = default);
}
