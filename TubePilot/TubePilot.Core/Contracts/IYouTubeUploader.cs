namespace TubePilot.Core.Contracts;

public interface IYouTubeUploader
{
    Task<YouTubeUploadResult> UploadAsync(
        YouTubeUploadRequest request,
        Func<int, Task> progressCallback,
        CancellationToken ct = default);
}
