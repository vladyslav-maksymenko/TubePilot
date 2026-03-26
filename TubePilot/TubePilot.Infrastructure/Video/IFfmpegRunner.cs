namespace TubePilot.Infrastructure.Video;

internal interface IFfmpegRunner
{
    Task<FfmpegProbeResult> ProbeAsync(string inputPath, CancellationToken ct = default);

    Task RunAsync(IReadOnlyList<string> arguments, double durationSeconds, Func<int, Task> progressCallback, CancellationToken ct = default);
}

internal sealed record FfmpegProbeResult(double DurationSeconds, int? Width, int? Height, bool HasVideo, bool HasAudio);
