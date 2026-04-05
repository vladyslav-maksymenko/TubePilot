using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Core.Utils;
using TubePilot.Infrastructure.Drive.Options;

namespace TubePilot.Infrastructure.Video;

internal sealed class FfmpegVideoProcessor(
    IOptionsMonitor<DriveOptions> optionsMonitor,
    IFfmpegRunner ffmpegRunner,
    ILogger<FfmpegVideoProcessor> logger) : IVideoProcessor
{
    private sealed record TransformPlan(VideoProcessingSummary Summary, string? QrOverlayPath);

    private static readonly string[] OrderedOptions =
    [
        "mirror",
        "reduce_audio",
        "slow_down",
        "speed_up",
        "color_correct",
        "slice",
        "slice_long",
        "qr_overlay",
        "rotate",
        "downscale_1080p"
    ];

    public async Task<IReadOnlyList<VideoProcessingResult>> ProcessAsync(
        string inputPath,
        HashSet<string> options,
        Func<VideoProcessingProgress, Task> progressCallback,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progressCallback);

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input video not found.", inputPath);
        }

        var lastProgress = new VideoProcessingProgress(-1, VideoProcessingStage.Init);
        async Task ReportAsync(VideoProcessingProgress progress)
        {
            if (progress.Percent == lastProgress.Percent && progress.Stage == lastProgress.Stage)
            {
                return;
            }

            lastProgress = progress;
            await progressCallback(progress);
        }

        await ReportAsync(new VideoProcessingProgress(0, VideoProcessingStage.Init));

        var selectedOptions = OrderedOptions.Where(options.Contains).ToArray();
        var sliceRequested = selectedOptions.Contains("slice") || selectedOptions.Contains("slice_long");
        var useLongSlice = selectedOptions.Contains("slice_long");
        var appliedOptions = selectedOptions.Where(option => option is not "slice" and not "slice_long").ToArray();

        var processedDir = Path.GetFullPath(optionsMonitor.CurrentValue.ProcessedDirectory);
        Directory.CreateDirectory(processedDir);

        var mediaInfo = await ffmpegRunner.ProbeAsync(inputPath, ct);
        if (!mediaInfo.HasVideo)
        {
            throw new InvalidOperationException($"Input video '{inputPath}' does not contain a video stream.");
        }

        logger.LogInformation(
            "Processing {InputPath} with {OptionCount} option(s). Slice={SliceRequested}, Media={Width}x{Height}, Duration={Duration:F2}s",
            inputPath,
            selectedOptions.Length,
            sliceRequested,
            mediaInfo.Width,
            mediaInfo.Height,
            mediaInfo.DurationSeconds);

        if (!sliceRequested)
        {
            var plan = BuildTransformPlan(inputPath, mediaInfo, appliedOptions, slice: null);
            var outputSuffix = BuildSuffix(plan.Summary);
            var outputPath = BuildFinalPath(processedDir, inputPath, outputSuffix);
            await RunStepAsync(
                mediaInfo.DurationSeconds,
                BuildTransformArguments(inputPath, outputPath, mediaInfo, plan),
                VideoProcessingStage.Transform,
                ReportAsync,
                completedSteps: 0,
                totalSteps: 1,
                ct);
            await ReportAsync(new VideoProcessingProgress(100, VideoProcessingStage.Finalizing));
            return [await BuildResultAsync(outputPath, 1, 1, plan.Summary, mediaInfo.DurationSeconds, ct)];
        }

        var slices = BuildSlices(mediaInfo.DurationSeconds, useLongSlice);
        if (slices.Count == 0)
        {
            logger.LogWarning("Slice mode requested for {InputPath}, but no slices were produced from {Duration:F2}s.", inputPath, mediaInfo.DurationSeconds);
            await ReportAsync(new VideoProcessingProgress(100, VideoProcessingStage.Finalizing));
            return [];
        }

        var totalSteps = slices.Count * (appliedOptions.Length == 0 ? 1 : 2);
        var completedSteps = 0;
        var outputs = new List<VideoProcessingResult>(slices.Count);
        var temporaryFiles = new List<string>();

        try
        {
            for (var index = 0; index < slices.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                var slice = slices[index];
                var sliceInfo = new VideoProcessingSliceInfo(slice.StartSeconds, slice.DurationSeconds);
                var sliceBaseName = BuildSliceBaseName(inputPath, index + 1);
                var partNumber = index + 1;
                var totalParts = slices.Count;

                if (appliedOptions.Length == 0)
                {
                    var finalSlicePath = BuildFinalPath(processedDir, inputPath, sliceBaseName);
                    await RunStepAsync(
                        slice.DurationSeconds,
                        BuildCutArguments(inputPath, finalSlicePath, slice.StartSeconds, slice.DurationSeconds),
                        VideoProcessingStage.Slicing,
                        ReportAsync,
                        completedSteps,
                        totalSteps,
                        ct);
                    completedSteps++;

                    var summary = new VideoProcessingSummary(
                        sliceInfo,
                        Mirror: false,
                        Volume: null,
                        Speed: null,
                        ColorCorrection: null,
                        QrOverlay: false,
                        Rotate: null,
                        Downscale: null);

                    outputs.Add(await BuildResultAsync(finalSlicePath, partNumber, totalParts, summary, slice.DurationSeconds, ct));
                    continue;
                }

                var tempCutPath = BuildTemporaryCutPath(inputPath, index + 1);
                temporaryFiles.Add(tempCutPath);

                await RunStepAsync(
                    slice.DurationSeconds,
                    BuildCutArguments(inputPath, tempCutPath, slice.StartSeconds, slice.DurationSeconds),
                    VideoProcessingStage.Slicing,
                    ReportAsync,
                    completedSteps,
                    totalSteps,
                    ct);
                completedSteps++;

                var plan = BuildTransformPlan(inputPath, mediaInfo, appliedOptions, sliceInfo);
                var transformedSuffix = BuildSuffix(plan.Summary);
                var finalOutputPath = BuildFinalPath(processedDir, inputPath, $"{sliceBaseName}_{transformedSuffix}");

                await RunStepAsync(
                    slice.DurationSeconds,
                    BuildTransformArguments(tempCutPath, finalOutputPath, mediaInfo with { DurationSeconds = slice.DurationSeconds }, plan),
                    VideoProcessingStage.Transform,
                    ReportAsync,
                    completedSteps,
                    totalSteps,
                    ct);
                completedSteps++;
                outputs.Add(await BuildResultAsync(finalOutputPath, partNumber, totalParts, plan.Summary, slice.DurationSeconds, ct));
            }

            await ReportAsync(new VideoProcessingProgress(100, VideoProcessingStage.Finalizing));
            return outputs;
        }
        finally
        {
            foreach (var temporaryFile in temporaryFiles)
            {
                TryDeleteQuietly(temporaryFile);
            }
        }
    }

    private async Task<VideoProcessingResult> BuildResultAsync(
        string outputPath,
        int partNumber,
        int totalParts,
        VideoProcessingSummary summary,
        double fallbackDurationSeconds,
        CancellationToken ct)
    {
        var sizeBytes = new FileInfo(outputPath).Length;
        var durationSeconds = fallbackDurationSeconds;

        try
        {
            var probed = await ffmpegRunner.ProbeAsync(outputPath, ct);
            durationSeconds = probed.DurationSeconds;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to probe {OutputPath} for duration. Falling back to {Fallback}s.", outputPath, fallbackDurationSeconds);
        }

        return new VideoProcessingResult(
            outputPath,
            partNumber,
            totalParts,
            durationSeconds,
            sizeBytes,
            summary);
    }

    private async Task RunStepAsync(
        double durationSeconds,
        IReadOnlyList<string> arguments,
        VideoProcessingStage stage,
        Func<VideoProcessingProgress, Task> overallProgressCallback,
        int completedSteps,
        int totalSteps,
        CancellationToken ct)
    {
        var lastReported = -1;
        await overallProgressCallback(new VideoProcessingProgress(ScaleProgress(completedSteps, totalSteps, 0), stage));
        await ffmpegRunner.RunAsync(
            arguments,
            durationSeconds,
            async stepPercent =>
            {
                var scaledPercent = ScaleProgress(completedSteps, totalSteps, Math.Min(stepPercent, 99));
                if (scaledPercent <= lastReported)
                {
                    return;
                }

                lastReported = scaledPercent;
                await overallProgressCallback(new VideoProcessingProgress(scaledPercent, stage));
            },
            ct);

        var completedPercent = totalSteps <= 0
            ? 100
            : Math.Clamp((int)Math.Round(((completedSteps + 1d) / totalSteps) * 100d), 0, 100);
        if (completedPercent > lastReported)
        {
            await overallProgressCallback(new VideoProcessingProgress(completedPercent, stage));
        }
    }

    private static int ScaleProgress(int completedSteps, int totalSteps, int currentStepPercent)
    {
        if (totalSteps <= 0)
        {
            return currentStepPercent >= 100 ? 100 : Math.Clamp(currentStepPercent, 0, 99);
        }

        var overall = ((completedSteps + (currentStepPercent / 100d)) / totalSteps) * 100d;
        return currentStepPercent >= 100
            ? Math.Clamp((int)Math.Round(((completedSteps + 1d) / totalSteps) * 100d), 0, 100)
            : Math.Clamp((int)Math.Floor(overall), 0, 99);
    }

    private static string BuildFinalPath(string processedDir, string inputPath, string suffix)
    {
        var baseName = FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(inputPath));
        var extension = Path.GetExtension(inputPath);
        return Path.Combine(processedDir, $"{baseName}_{suffix}{extension}");
    }

    private static string BuildSliceBaseName(string inputPath, int partNumber)
        => $"part{partNumber:00}";

    private static string BuildTemporaryCutPath(string inputPath, int partNumber)
    {
        var extension = Path.GetExtension(inputPath);
        var baseName = FileNameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(inputPath));
        return Path.Combine(Path.GetTempPath(), $"TubePilot_{baseName}_part{partNumber:00}_{Guid.NewGuid():N}{extension}");
    }

    private static TransformPlan BuildTransformPlan(
        string assetLookupPath,
        FfmpegProbeResult mediaInfo,
        string[] appliedOptions,
        VideoProcessingSliceInfo? slice)
    {
        var mirror = false;
        VideoProcessingVolumeInfo? volume = null;
        VideoProcessingSpeedInfo? speed = null;
        VideoProcessingColorCorrectionInfo? colorCorrection = null;
        var qrOverlay = false;
        string? qrOverlayPath = null;
        VideoProcessingRotateInfo? rotate = null;
        VideoProcessingDownscaleInfo? downscale = null;
        var skippedReasons = new List<string>();

        foreach (var option in appliedOptions)
        {
            switch (option)
            {
                case "mirror":
                    mirror = true;
                    break;
                case "reduce_audio":
                    var volumeReduction = Random.Shared.NextDouble() * 0.10 + 0.80;
                    volume = new VideoProcessingVolumeInfo(volumeReduction);
                    break;
                case "slow_down":
                    var slowdownFactor = Random.Shared.NextDouble() * 0.03 + 1.04;
                    speed = new VideoProcessingSpeedInfo(1d / slowdownFactor);
                    break;
                case "speed_up":
                    if (speed is not null)
                    {
                        skippedReasons.Add("⚡ Speed up — пропущено (конфлікт з slow down)");
                        break;
                    }
                    var speedupFactor = Random.Shared.NextDouble() * 0.02 + 1.03;
                    speed = new VideoProcessingSpeedInfo(speedupFactor);
                    break;
                case "color_correct":
                    var saturation = 1.0 + Random.Shared.NextDouble() * 0.03 + 0.04;
                    var brightness = -(Random.Shared.NextDouble() * 0.03 + 0.04);
                    var gamma = 1.0 - (Random.Shared.NextDouble() * 0.02 + 0.01);
                    colorCorrection = new VideoProcessingColorCorrectionInfo(saturation, brightness, gamma);
                    break;
                case "qr_overlay":
                    qrOverlay = true;
                    qrOverlayPath = ResolveQrOverlayPath(assetLookupPath);
                    break;
                case "rotate":
                    var degrees = Random.Shared.NextDouble() * 2d + 3d;
                    var zoom = Random.Shared.NextDouble() * 0.05 + 1.10;
                    rotate = new VideoProcessingRotateInfo(degrees, zoom);
                    break;
                case "downscale_1080p":
                    if (mediaInfo.Height is > 1080)
                    {
                        downscale = new VideoProcessingDownscaleInfo(1080);
                    }
                    else
                    {
                        skippedReasons.Add($"📐 Даунскейл 1080p — пропущено (відео вже {mediaInfo.Height}p)");
                    }
                    break;
            }
        }

        var summary = new VideoProcessingSummary(
            slice,
            mirror,
            volume,
            speed,
            colorCorrection,
            qrOverlay,
            rotate,
            downscale,
            skippedReasons);

        return new TransformPlan(summary, qrOverlayPath);
    }

    private static string BuildSuffix(VideoProcessingSummary summary)
    {
        var suffixParts = new List<string>();
        string? speedSuffix = null;

        if (summary.Mirror)
        {
            suffixParts.Add("mir");
        }

        if (summary.Volume is not null)
        {
            suffixParts.Add(FormattableString.Invariant($"vol{(int)Math.Round(summary.Volume.Value.Factor * 100)}"));
        }

        if (summary.ColorCorrection is not null)
        {
            suffixParts.Add("color");
        }

        if (summary.QrOverlay)
        {
            suffixParts.Add("qr");
        }

        if (summary.Rotate is not null)
        {
            suffixParts.Add(FormattableString.Invariant($"rot{(int)Math.Round(summary.Rotate.Value.Degrees)}"));
        }

        if (summary.Downscale is not null)
        {
            suffixParts.Add("1080p");
        }

        if (summary.Speed is not null)
        {
            var speedFactor = summary.Speed.Value.SpeedFactor;
            if (speedFactor > 1.0001)
            {
                speedSuffix = FormattableString.Invariant($"fast{(int)Math.Round(speedFactor * 100)}");
            }
            else if (speedFactor < 0.9999)
            {
                speedSuffix = FormattableString.Invariant($"slow{(int)Math.Round((1d / speedFactor) * 100)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(speedSuffix))
        {
            suffixParts.Add(speedSuffix);
        }

        return suffixParts.Count == 0 ? "processed" : string.Join('_', suffixParts);
    }

    private static IReadOnlyList<string> BuildCutArguments(string inputPath, string outputPath, double startSeconds, double durationSeconds)
    {
        var arguments = new List<string>
        {
            "-i", inputPath,
            "-ss", startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            outputPath
        };

        return arguments;
    }

    private static IReadOnlyList<string> BuildTransformArguments(
        string inputPath,
        string outputPath,
        FfmpegProbeResult mediaInfo,
        TransformPlan plan)
    {
        var arguments = new List<string> { "-i", inputPath };
        var filterParts = new List<string>();
        var videoFilters = new List<string>();
        var audioFilters = new List<string>();
        var summary = plan.Summary;
        var qrOverlayPath = plan.QrOverlayPath;

        if (summary.Mirror)
        {
            videoFilters.Add("hflip");
        }

        if (summary.Volume is not null)
        {
            audioFilters.Add(FormattableString.Invariant($"volume={summary.Volume.Value.Factor:0.00}"));
        }

        if (summary.Speed is not null)
        {
            var speedFactor = summary.Speed.Value.SpeedFactor;
            var playbackFactor = 1d / speedFactor;
            videoFilters.Insert(0, FormattableString.Invariant($"setpts={playbackFactor:0.0000}*PTS"));
            audioFilters.Insert(0, FormattableString.Invariant($"atempo={speedFactor:0.0000}"));
        }

        if (summary.ColorCorrection is not null)
        {
            var color = summary.ColorCorrection.Value;
            videoFilters.Add(FormattableString.Invariant(
                $"eq=saturation={color.Saturation:0.000}:brightness={color.Brightness:0.000}:gamma={color.Gamma:0.000}"));
        }

        if (summary.Rotate is not null)
        {
            var rotation = summary.Rotate.Value;
            var radians = rotation.Degrees * Math.PI / 180d;
            videoFilters.Add(FormattableString.Invariant($"scale=iw*{rotation.Zoom:0.00}:ih*{rotation.Zoom:0.00}"));
            videoFilters.Add(FormattableString.Invariant($"rotate={radians:0.0000}:fillcolor=black"));
            videoFilters.Add(FormattableString.Invariant($"crop=iw/{rotation.Zoom:0.00}:ih/{rotation.Zoom:0.00}"));
        }

        if (summary.Downscale is not null)
        {
            videoFilters.Add("scale=-2:1080");
        }

        var videoNeedsReencode = videoFilters.Count > 0 || qrOverlayPath is not null;
        var audioNeedsReencode = audioFilters.Count > 0;

        if (qrOverlayPath is not null)
        {
            arguments.Add("-i");
            arguments.Add(qrOverlayPath);
        }

        if (videoNeedsReencode || audioNeedsReencode)
        {
            if (videoFilters.Count > 0)
            {
                filterParts.Add(FormattableString.Invariant($"[0:v]{string.Join(',', videoFilters)}[vfiltered]"));
            }

            var finalVideoLabel = "0:v";

            if (qrOverlayPath is not null)
            {
                filterParts.Add("[1:v]scale=iw*0.25:-1[qr]");
                var overlayInputLabel = videoFilters.Count > 0 ? "vfiltered" : "0:v";
                filterParts.Add($"[{overlayInputLabel}][qr]overlay=W-w-20:H-h-20:enable='between(t,0,10)'[vout]");
                finalVideoLabel = "vout";
            }
            else if (videoFilters.Count > 0)
            {
                finalVideoLabel = "vfiltered";
            }

            if (audioFilters.Count > 0 && mediaInfo.HasAudio)
            {
                filterParts.Add(FormattableString.Invariant($"[0:a]{string.Join(',', audioFilters)}[aout]"));
            }

            if (filterParts.Count > 0)
            {
                arguments.Add("-filter_complex");
                arguments.Add(string.Join(';', filterParts));
            }

            if (finalVideoLabel == "0:v")
            {
                arguments.Add("-map");
                arguments.Add("0:v:0");
            }
            else
            {
                arguments.Add("-map");
                arguments.Add($"[{finalVideoLabel}]");
            }

            if (mediaInfo.HasAudio)
            {
                if (audioFilters.Count > 0)
                {
                    arguments.Add("-map");
                    arguments.Add("[aout]");
                }
                else
                {
                    arguments.Add("-map");
                    arguments.Add("0:a:0?");
                }
            }
            else
            {
                arguments.Add("-an");
            }

            if (finalVideoLabel == "0:v")
            {
                arguments.Add("-c:v");
                arguments.Add("copy");
            }
            else
            {
                arguments.Add("-c:v");
                arguments.Add("libx264");
                arguments.Add("-crf");
                arguments.Add("23");
                arguments.Add("-preset");
                arguments.Add("medium");
                arguments.Add("-pix_fmt");
                arguments.Add("yuv420p");
            }

            if (mediaInfo.HasAudio && audioFilters.Count > 0)
            {
                arguments.Add("-c:a");
                arguments.Add("aac");
                arguments.Add("-b:a");
                arguments.Add("192k");
            }
            else if (mediaInfo.HasAudio)
            {
                arguments.Add("-c:a");
                arguments.Add("copy");
            }
        }
        else
        {
            arguments.Add("-c");
            arguments.Add("copy");
        }

        arguments.Add(outputPath);
        return arguments;
    }

    private static IReadOnlyList<(double StartSeconds, double DurationSeconds)> BuildSlices(double durationSeconds, bool useLongSlice)
    {
        var minSliceDuration = useLongSlice ? 310d : 150d;
        var maxSliceDuration = useLongSlice ? 430d : 190d;

        var slices = new List<(double StartSeconds, double DurationSeconds)>();
        var startSeconds = 0d;
        while (startSeconds < durationSeconds)
        {
            var remainingDuration = durationSeconds - startSeconds;
            if (remainingDuration < minSliceDuration)
            {
                break;
            }

            if (remainingDuration <= maxSliceDuration)
            {
                slices.Add((startSeconds, remainingDuration));
                break;
            }

            var maxPickDuration = Math.Min(maxSliceDuration, remainingDuration - minSliceDuration);
            if (maxPickDuration < minSliceDuration)
            {
                slices.Add((startSeconds, maxSliceDuration));
                break;
            }

            var sliceDuration = Random.Shared.NextDouble() * (maxPickDuration - minSliceDuration) + minSliceDuration;
            slices.Add((startSeconds, sliceDuration));
            startSeconds += sliceDuration;
        }

        return slices;
    }

    private static string ResolveQrOverlayPath(string inputPath)
    {
        var candidates = new List<string>();
        var inputDirectory = Path.GetDirectoryName(inputPath);

        if (!string.IsNullOrWhiteSpace(inputDirectory))
        {
            candidates.Add(Path.Combine(inputDirectory, "qr.png"));
            candidates.Add(Path.Combine(inputDirectory, "assets", "qr.png"));
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "qr.png"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "assets", "qr.png"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "qr.png"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "assets", "qr.png"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "QR overlay image not found. Expected `qr.png` (or `assets/qr.png`) near the input video, in the current directory, or in the application directory.");
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
