namespace TubePilot.Infrastructure.Telegram.Options;

public sealed record PublishingOptions
{
    public const string SectionName = "Publishing";

    public string TimeZoneId { get; init; } = "Europe/Kiev";
}
