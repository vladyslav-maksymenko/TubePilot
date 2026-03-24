namespace TubePilot.Infrastructure.Drive.Options;

public sealed record DriveOptions
{
    public const string SectionName = "GoogleDrive";

    public string? FolderId { get; init; }

    public string DownloadDirectory { get; init; } = "downloads";

    public string ProcessedDirectory { get; init; } = "processed";

    public int PollingIntervalSeconds { get; init; } = 30;

    public ServiceAccountOptions? ServiceAccount { get; init; }
}