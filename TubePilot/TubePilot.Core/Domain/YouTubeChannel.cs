namespace TubePilot.Core.Domain;

public sealed class YouTubeChannel
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? YouTubeChannelId { get; set; }
    public string? RefreshToken { get; set; }
    public string? DriveFolderId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}
