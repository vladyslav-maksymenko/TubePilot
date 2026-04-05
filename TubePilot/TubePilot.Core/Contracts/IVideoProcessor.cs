namespace TubePilot.Core.Contracts;

public interface IVideoProcessor
{
    Task<IReadOnlyList<VideoProcessingResult>> ProcessAsync(
        string inputPath,
        HashSet<string> options,
        Func<VideoProcessingProgress, Task> progressCallback,
        CancellationToken ct = default);
}
