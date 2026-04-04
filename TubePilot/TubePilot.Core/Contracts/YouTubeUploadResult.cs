namespace TubePilot.Core.Contracts;

public sealed record YouTubeUploadResult(
    string VideoId,
    string YouTubeUrl,
    YouTubeUploadStatus Status,
    DateTimeOffset? ScheduledAtUtc);
