namespace TubePilot.Infrastructure.Telegram.Options;

public sealed record TelegramOptions
{
    public const string SectionName = "Telegram";
    
    public required string BotToken { get; init; }

    public string BaseUrl { get; init; } = "http://localhost:5000";

    public long? AllowedChatId { get; init; }

    public int MaxConcurrentJobs { get; init; } = 1;

    public bool DevCommandsEnabled { get; init; } = false;

    public int DevSimulatedProcessingSeconds { get; init; } = 30;
}
