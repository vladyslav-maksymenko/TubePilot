using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramUiClient(ITelegramBotClient botClient) : ITelegramUiClient
{
    private static bool IsMessageNotModified(ApiRequestException ex)
        => ex.ErrorCode == 400 &&
           ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    public async Task<int> SendMessageAsync(
        long chatId,
        string text,
        ParseMode? parseMode = null,
        int? replyToMessageId = null,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken ct = default)
    {
        ReplyParameters? replyParameters = null;
        if (replyToMessageId is not null && replyToMessageId.Value > 0)
        {
            replyParameters = new ReplyParameters
            {
                MessageId = replyToMessageId.Value,
                AllowSendingWithoutReply = true
            };
        }

        var message = parseMode is null
            ? await botClient.SendMessage(chatId, text, replyParameters: replyParameters, replyMarkup: replyMarkup, cancellationToken: ct)
            : await botClient.SendMessage(chatId, text, parseMode: parseMode.Value, replyParameters: replyParameters, replyMarkup: replyMarkup, cancellationToken: ct);

        return message.MessageId;
    }

    public async Task<int> SendMessageWithReplyKeyboardAsync(
        long chatId,
        string text,
        ReplyKeyboardMarkup replyKeyboard,
        ParseMode? parseMode = null,
        CancellationToken ct = default)
    {
        var message = parseMode is null
            ? await botClient.SendMessage(chatId, text, replyMarkup: replyKeyboard, cancellationToken: ct)
            : await botClient.SendMessage(chatId, text, parseMode: parseMode.Value, replyMarkup: replyKeyboard, cancellationToken: ct);

        return message.MessageId;
    }

    public async Task EditMessageTextAsync(
        long chatId,
        int messageId,
        string text,
        ParseMode? parseMode = null,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken ct = default)
    {
        if (parseMode is null)
        {
            try
            {
                _ = await botClient.EditMessageText(chatId, messageId, text, replyMarkup: replyMarkup, cancellationToken: ct);
            }
            catch (ApiRequestException ex) when (IsMessageNotModified(ex))
            {
                // Benign: happens when we attempt to re-apply identical text/markup (race/double callbacks).
            }
            return;
        }

        try
        {
            _ = await botClient.EditMessageText(chatId, messageId, text, parseMode: parseMode.Value, replyMarkup: replyMarkup, cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            // Benign: happens when we attempt to re-apply identical text/markup (race/double callbacks).
        }
    }

    public Task AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text = null,
        bool showAlert = false,
        CancellationToken ct = default)
        => botClient.AnswerCallbackQuery(callbackQueryId, text, showAlert: showAlert, cancellationToken: ct);
}
