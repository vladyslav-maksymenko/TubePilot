namespace TubePilot.Infrastructure.Video;

internal sealed record FfmpegProbeResult(double DurationSeconds, int? Width, int? Height, bool HasVideo, bool HasAudio);
