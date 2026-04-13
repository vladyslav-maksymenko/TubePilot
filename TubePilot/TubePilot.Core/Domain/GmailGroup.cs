namespace TubePilot.Core.Domain;

public sealed class GmailGroup
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    public List<YouTubeChannel> Channels { get; init; } = [];
    public double QuotaUsedToday { get; set; }
    public DateTimeOffset QuotaResetAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}
