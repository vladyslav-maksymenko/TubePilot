namespace TubePilot.Infrastructure.Telegram.Options;

public sealed record PublishingOptions
{
    public const string SectionName = "Publishing";

    public string TimeZoneId { get; init; } = "Europe/Kiev";

    /// <summary>
    /// Default local (per <see cref="TimeZoneId"/>) time-of-day for scheduled publishing.
    /// Format: HH:mm (e.g. 10:00).
    /// </summary>
    public string DailyPublishTime { get; init; } = "10:00";

    /// <summary>
    /// Channel names shown in the Telegram publish wizard.
    /// </summary>
    public IReadOnlyList<string> YouTubeChannels { get; init; } = ["Default"];
}
