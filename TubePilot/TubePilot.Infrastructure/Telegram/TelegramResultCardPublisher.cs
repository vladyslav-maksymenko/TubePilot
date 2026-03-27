using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramResultCardPublisher(
    ITelegramResultCardClient client,
    ITelegramResultThumbnailGenerator thumbnailGenerator,
    IDelay delay,
    ILogger<TelegramResultCardPublisher> logger)
{
    private static readonly TimeSpan ResultCardThrottleDelay = TimeSpan.FromMilliseconds(1500);

    public async Task<IReadOnlyList<Message>> SendResultCardsAsync(
        long chatId,
        IReadOnlyList<Models.PublishedResultContext> contexts,
        CancellationToken ct)
    {
        if (contexts.Count == 0)
        {
            return Array.Empty<Message>();
        }

        var messages = new List<Message>(contexts.Count);
        for (var index = 0; index < contexts.Count; index++)
        {
            var message = await SendResultCardAsync(chatId, contexts[index], ct);
            messages.Add(message);

            if (index < contexts.Count - 1)
            {
                await delay.DelayAsync(ResultCardThrottleDelay, ct);
            }
        }

        return messages;
    }

    private static string BuildResultMessage(Models.PublishedResultContext context)
        => TelegramSegmentResultMessageBuilder.BuildResultMessage(context);

    private static InlineKeyboardMarkup BuildResultKeyboard(Models.PublishedResultContext context)
    {
        var buttons = new List<IEnumerable<InlineKeyboardButton>>();

        if (!string.IsNullOrWhiteSpace(context.PublicUrl) && TelegramUrlSafety.IsTelegramSafeButtonUrl(context.PublicUrl))
        {
            buttons.Add([InlineKeyboardButton.WithUrl("Смотреть", context.PublicUrl)]);
        }

        buttons.Add([InlineKeyboardButton.WithCallbackData("📤 Опублікувати на YouTube", $"{TelegramBotService.PublishResultPrefix}publish")]);

        return new InlineKeyboardMarkup(buttons);
    }

    private async Task<Message> SendResultCardAsync(long chatId, Models.PublishedResultContext context, CancellationToken ct)
    {
        var caption = BuildResultMessage(context);
        var keyboard = BuildResultKeyboard(context);
        string? thumbPath = null;

        try
        {
            thumbPath = await thumbnailGenerator.TryGenerateAsync(context.ResultFilePath, ct);
            if (!string.IsNullOrWhiteSpace(thumbPath) && File.Exists(thumbPath))
            {
                try
                {
                    await using var stream = new FileStream(thumbPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                    var input = InputFile.FromStream(stream, Path.GetFileName(thumbPath));

                    return await ExecuteWithRateLimitRetryAsync(
                        () => client.SendPhotoAsync(chatId, input, caption, keyboard, ct),
                        ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to send thumbnail card for {FileName}; falling back to text.", context.ResultFileName);
                }
            }

            return await ExecuteWithRateLimitRetryAsync(
                () => client.SendMessageAsync(chatId, caption, keyboard, ct),
                ct);
        }
        finally
        {
            TryDeleteQuietly(thumbPath);
        }
    }

    private async Task<T> ExecuteWithRateLimitRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && attempt < maxAttempts)
            {
                var retryAfterSeconds = ex.Parameters?.RetryAfter ?? 2 * attempt;
                logger.LogDebug(
                    ex,
                    "Telegram flood control (429). Retrying after {RetryAfterSeconds}s (attempt {Attempt}/{MaxAttempts}).",
                    retryAfterSeconds,
                    attempt,
                    maxAttempts);
                await delay.DelayAsync(TimeSpan.FromSeconds(Math.Clamp(retryAfterSeconds, 1, 60)), ct);
            }
        }

        throw new InvalidOperationException("Failed to execute Telegram API call after retries.");
    }

    private static void TryDeleteQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
