namespace TubePilot.Core.Contracts;

public sealed record YouTubeUploadRequest(
    string VideoFilePath,
    string Title,
    string Description,
    IReadOnlyList<string>? Tags = null,
    YouTubeVideoVisibility Visibility = YouTubeVideoVisibility.Public,
    DateTimeOffset? ScheduledPublishAtUtc = null,
    string? ThumbnailFilePath = null,
    string? CategoryId = null,
    YouTubeUploadCredentials? Credentials = null);
