namespace TubePilot.Infrastructure.Telegram.Options;

public sealed record TelegramOptions
{
    public const string SectionName = "Telegram";
    
    public required string BotToken { get; init; }

    public string BaseUrl { get; init; } = "http://localhost:5000";

    public string? NgrokAuthToken { get; init; }

    public long? AllowedChatId { get; init; }
}