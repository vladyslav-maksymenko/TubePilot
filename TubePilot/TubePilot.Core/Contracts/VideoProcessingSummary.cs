namespace TubePilot.Core.Contracts;

public sealed record VideoProcessingSummary(
    VideoProcessingSliceInfo? Slice,
    bool Mirror,
    VideoProcessingVolumeInfo? Volume,
    VideoProcessingSpeedInfo? Speed,
    VideoProcessingColorCorrectionInfo? ColorCorrection,
    bool QrOverlay,
    VideoProcessingRotateInfo? Rotate,
    VideoProcessingDownscaleInfo? Downscale,
    IReadOnlyList<string> SkippedReasons)
{
    public VideoProcessingSummary(
        VideoProcessingSliceInfo? Slice,
        bool Mirror,
        VideoProcessingVolumeInfo? Volume,
        VideoProcessingSpeedInfo? Speed,
        VideoProcessingColorCorrectionInfo? ColorCorrection,
        bool QrOverlay,
        VideoProcessingRotateInfo? Rotate,
        VideoProcessingDownscaleInfo? Downscale)
        : this(Slice, Mirror, Volume, Speed, ColorCorrection, QrOverlay, Rotate, Downscale, []) { }
}
