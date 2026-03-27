namespace TubePilot.Infrastructure.Telegram;

internal interface ITelegramResultThumbnailGenerator
{
    Task<string?> TryGenerateAsync(string videoPath, CancellationToken ct);
}

