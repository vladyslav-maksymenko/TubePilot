namespace TubePilot.Core.Contracts;

public sealed record VideoProcessingSummary(
    VideoProcessingSliceInfo? Slice,
    bool Mirror,
    VideoProcessingVolumeInfo? Volume,
    VideoProcessingSpeedInfo? Speed,
    VideoProcessingColorCorrectionInfo? ColorCorrection,
    bool QrOverlay,
    VideoProcessingRotateInfo? Rotate,
    VideoProcessingDownscaleInfo? Downscale);
