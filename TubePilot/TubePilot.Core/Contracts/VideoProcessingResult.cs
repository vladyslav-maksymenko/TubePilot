namespace TubePilot.Core.Contracts;

public readonly record struct VideoProcessingSliceInfo(double StartSeconds, double DurationSeconds);

public readonly record struct VideoProcessingVolumeInfo(double Factor);

public readonly record struct VideoProcessingSpeedInfo(double SpeedFactor);

public readonly record struct VideoProcessingColorCorrectionInfo(double Saturation, double Brightness, double Gamma);

public readonly record struct VideoProcessingRotateInfo(double Degrees, double Zoom);

public readonly record struct VideoProcessingDownscaleInfo(int TargetHeight);

public sealed record VideoProcessingSummary(
    VideoProcessingSliceInfo? Slice,
    bool Mirror,
    VideoProcessingVolumeInfo? Volume,
    VideoProcessingSpeedInfo? Speed,
    VideoProcessingColorCorrectionInfo? ColorCorrection,
    bool QrOverlay,
    VideoProcessingRotateInfo? Rotate,
    VideoProcessingDownscaleInfo? Downscale);

public readonly record struct VideoProcessingResult(
    string OutputPath,
    int PartNumber,
    int TotalParts,
    double DurationSeconds,
    long SizeBytes,
    VideoProcessingSummary Summary);

