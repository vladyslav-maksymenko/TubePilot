using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Telegram;
using TubePilot.Infrastructure.Telegram.Models;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramSegmentResultMessageBuilderTests
{
    [Fact]
    public void BuildResultMessage_IncludesPartDurationSizeAndAppliedOptions()
    {
        var summary = new VideoProcessingSummary(
            Slice: new VideoProcessingSliceInfo(StartSeconds: 0, DurationSeconds: 190),
            Mirror: true,
            Volume: new VideoProcessingVolumeInfo(0.85),
            Speed: new VideoProcessingSpeedInfo(1.04),
            ColorCorrection: new VideoProcessingColorCorrectionInfo(1.05, -0.05, 0.98),
            QrOverlay: true,
            Rotate: new VideoProcessingRotateInfo(4.2, 1.12),
            Downscale: new VideoProcessingDownscaleInfo(1080));

        var context = new PublishedResultContext(
            SourceFileName: "source.mp4",
            ResultFileName: "part01.mp4",
            ResultFilePath: @"C:\out\part01.mp4",
            PublicUrl: "https://example.test/play/part01.mp4",
            PartNumber: 1,
            TotalParts: 3,
            DurationSeconds: 190,
            SizeBytes: (long)(47.2 * 1024 * 1024),
            ProcessingSummary: summary);

        var message = TelegramSegmentResultMessageBuilder.BuildResultMessage(context);

        Assert.Contains("Part", message, StringComparison.Ordinal);
        Assert.Contains("1/3", message, StringComparison.Ordinal);
        Assert.Contains("03:10", message, StringComparison.Ordinal);
        Assert.Contains("47.2 MB", message, StringComparison.Ordinal);

        Assert.Contains("Дзеркало", message, StringComparison.Ordinal);
        Assert.Contains("Гучність", message, StringComparison.Ordinal);
        Assert.Contains("-15%", message, StringComparison.Ordinal);
        Assert.Contains("x0.85", message, StringComparison.Ordinal);
        Assert.Contains("x1.04", message, StringComparison.Ordinal);
        Assert.Contains("Поворот", message, StringComparison.Ordinal);
        Assert.Contains("1080p", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildResultMessage_HtmlEncodesFileName()
    {
        var summary = new VideoProcessingSummary(
            Slice: null,
            Mirror: false,
            Volume: null,
            Speed: null,
            ColorCorrection: null,
            QrOverlay: false,
            Rotate: null,
            Downscale: null);

        var context = new PublishedResultContext(
            SourceFileName: "source.mp4",
            ResultFileName: "my<vid>.mp4",
            ResultFilePath: @"C:\out\my<vid>.mp4",
            PublicUrl: null,
            PartNumber: 1,
            TotalParts: 1,
            DurationSeconds: 1,
            SizeBytes: 1,
            ProcessingSummary: summary);

        var message = TelegramSegmentResultMessageBuilder.BuildResultMessage(context);

        Assert.Contains("my&lt;vid&gt;.mp4", message, StringComparison.Ordinal);
        Assert.DoesNotContain("my<vid>.mp4", message, StringComparison.Ordinal);
    }
}

