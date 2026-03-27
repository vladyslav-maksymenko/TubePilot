using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramResultCardClient(ITelegramBotClient botClient) : ITelegramResultCardClient
{
    public Task<Message> SendPhotoAsync(
        long chatId,
        InputFile photo,
        string caption,
        InlineKeyboardMarkup replyMarkup,
        CancellationToken ct)
        => botClient.SendPhoto(
            chatId,
            photo: photo,
            caption: caption,
            parseMode: ParseMode.Html,
            replyMarkup: replyMarkup,
            cancellationToken: ct);

    public Task<Message> SendMessageAsync(
        long chatId,
        string text,
        InlineKeyboardMarkup replyMarkup,
        CancellationToken ct)
        => botClient.SendMessage(
            chatId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: replyMarkup,
            cancellationToken: ct);
}
