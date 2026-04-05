using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace TubePilot.Infrastructure.Telegram;

internal interface ITelegramResultCardClient
{
    Task<Message> SendPhotoAsync(
        long chatId,
        InputFile photo,
        string caption,
        InlineKeyboardMarkup replyMarkup,
        CancellationToken ct);

    Task<Message> SendMessageAsync(
        long chatId,
        string text,
        InlineKeyboardMarkup replyMarkup,
        CancellationToken ct);
}
