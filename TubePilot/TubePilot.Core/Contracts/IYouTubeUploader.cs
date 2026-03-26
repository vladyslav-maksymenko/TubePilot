namespace TubePilot.Core.Contracts;

public interface IYouTubeUploader
{
    Task<YouTubeUploadResult> UploadAsync(
        YouTubeUploadRequest request,
        Func<int, Task> progressCallback,
        CancellationToken ct = default);
}

public sealed record YouTubeUploadRequest(
    string VideoFilePath,
    string Title,
    string Description,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? ScheduledPublishAtUtc = null,
    string? ThumbnailFilePath = null,
    string? CategoryId = null);

public enum YouTubeUploadStatus
{
    Published,
    Scheduled
}

public sealed record YouTubeUploadResult(
    string VideoId,
    string YouTubeUrl,
    YouTubeUploadStatus Status,
    DateTimeOffset? ScheduledAtUtc);
