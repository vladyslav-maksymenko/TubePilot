namespace TubePilot.Infrastructure.Drive.Options;

public sealed record DriveOptions
{
    public const string SectionName = "GoogleDrive";

    public string? FolderId { get; init; }

    public string DownloadDirectory { get; init; } = "downloads";

    public ServiceAccountOptions? ServiceAccount { get; init; }
}