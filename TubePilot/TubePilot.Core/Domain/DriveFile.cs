namespace TubePilot.Core.Domain;

public sealed record DriveFile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string MimeType { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}