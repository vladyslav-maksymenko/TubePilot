namespace TubePilot.Infrastructure.Telegram.Options;

public sealed record TelegramOptions
{
    public const string SectionName = "Telegram";
    
    public required string BotToken { get; init; }
}