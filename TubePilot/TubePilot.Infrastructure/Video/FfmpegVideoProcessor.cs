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

    public async Task<IReadOnlyList<string>> ProcessAsync(
        string inputPath,
        HashSet<string> options,
        Func<int, Task> progressCallback,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progressCallback);

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input video not found.", inputPath);
        }

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
            var outputSuffix = BuildSuffix(appliedOptions, mediaInfo);
            var outputPath = BuildFinalPath(processedDir, inputPath, outputSuffix);
            await RunStepAsync(
                mediaInfo.DurationSeconds,
                BuildTransformArguments(inputPath, outputPath, mediaInfo, appliedOptions),
                progressCallback,
                completedSteps: 0,
                totalSteps: 1,
                ct);
            return [outputPath];
        }

        var slices = BuildSlices(mediaInfo.DurationSeconds, useLongSlice);
        if (slices.Count == 0)
        {
            logger.LogWarning("Slice mode requested for {InputPath}, but no slices were produced from {Duration:F2}s.", inputPath, mediaInfo.DurationSeconds);
            await progressCallback(100);
            return [];
        }

        var totalSteps = slices.Count * (appliedOptions.Length == 0 ? 1 : 2);
        var completedSteps = 0;
        var outputs = new List<string>(slices.Count);
        var temporaryFiles = new List<string>();

        try
        {
            for (var index = 0; index < slices.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                var slice = slices[index];
                var sliceBaseName = BuildSliceBaseName(inputPath, index + 1);

                if (appliedOptions.Length == 0)
                {
                    var finalSlicePath = BuildFinalPath(processedDir, inputPath, sliceBaseName);
                    await RunStepAsync(
                        slice.DurationSeconds,
                        BuildCutArguments(inputPath, finalSlicePath, slice.StartSeconds, slice.DurationSeconds),
                        progressCallback,
                        completedSteps,
                        totalSteps,
                        ct);
                    completedSteps++;
                    outputs.Add(finalSlicePath);
                    continue;
                }

                var tempCutPath = BuildTemporaryCutPath(inputPath, index + 1);
                temporaryFiles.Add(tempCutPath);

                await RunStepAsync(
                    slice.DurationSeconds,
                    BuildCutArguments(inputPath, tempCutPath, slice.StartSeconds, slice.DurationSeconds),
                    progressCallback,
                    completedSteps,
                    totalSteps,
                    ct);
                completedSteps++;

                var transformedSuffix = BuildSuffix(appliedOptions, mediaInfo);
                var finalOutputPath = BuildFinalPath(processedDir, inputPath, $"{sliceBaseName}_{transformedSuffix}");

                await RunStepAsync(
                    slice.DurationSeconds,
                    BuildTransformArguments(tempCutPath, finalOutputPath, mediaInfo with { DurationSeconds = slice.DurationSeconds }, appliedOptions),
                    progressCallback,
                    completedSteps,
                    totalSteps,
                    ct);
                completedSteps++;
                outputs.Add(finalOutputPath);
            }

            await progressCallback(100);
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

    private async Task RunStepAsync(
        double durationSeconds,
        IReadOnlyList<string> arguments,
        Func<int, Task> overallProgressCallback,
        int completedSteps,
        int totalSteps,
        CancellationToken ct)
    {
        var lastReported = -1;
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
                await overallProgressCallback(scaledPercent);
            },
            ct);

        var completedPercent = totalSteps <= 0
            ? 100
            : Math.Clamp((int)Math.Round(((completedSteps + 1d) / totalSteps) * 100d), 0, 100);
        if (completedPercent > lastReported)
        {
            await overallProgressCallback(completedPercent);
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

    private static string BuildSuffix(string[] appliedOptions, FfmpegProbeResult mediaInfo)
    {
        var suffixParts = new List<string>();
        string? speedSuffix = null;
        foreach (var option in appliedOptions)
        {
            switch (option)
            {
                case "mirror":
                    suffixParts.Add("mir");
                    break;
                case "reduce_audio":
                    var volumeReduction = Random.Shared.NextDouble() * 0.10 + 0.80;
                    suffixParts.Add($"vol{(int)Math.Round(volumeReduction * 100)}");
                    break;
                case "slow_down":
                    var slowdownFactor = Random.Shared.NextDouble() * 0.03 + 1.04;
                    speedSuffix = $"slow{(int)Math.Round(slowdownFactor * 100)}";
                    break;
                case "speed_up":
                    var speedupFactor = Random.Shared.NextDouble() * 0.02 + 1.03;
                    speedSuffix = $"fast{(int)Math.Round(speedupFactor * 100)}";
                    break;
                case "color_correct":
                    suffixParts.Add("color");
                    break;
                case "qr_overlay":
                    suffixParts.Add("qr");
                    break;
                case "rotate":
                    var degrees = Random.Shared.NextDouble() * 2d + 3d;
                    suffixParts.Add($"rot{(int)Math.Round(degrees)}");
                    break;
                case "downscale_1080p":
                    if (mediaInfo.Height is > 1080)
                    {
                        suffixParts.Add("1080p");
                    }
                    break;
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
        string[] appliedOptions)
    {
        var arguments = new List<string> { "-i", inputPath };
        var filterParts = new List<string>();
        var videoFilters = new List<string>();
        var audioFilters = new List<string>();
        double? playbackFactor = null;
        double? audioTempoFactor = null;
        string? qrOverlayPath = null;

        foreach (var option in appliedOptions)
        {
            switch (option)
            {
                case "mirror":
                    videoFilters.Add("hflip");
                    break;
                case "reduce_audio":
                    var volumeReduction = Random.Shared.NextDouble() * 0.10 + 0.80;
                    audioFilters.Add(FormattableString.Invariant($"volume={volumeReduction:0.00}"));
                    break;
                case "slow_down":
                    var slowdownFactor = Random.Shared.NextDouble() * 0.03 + 1.04;
                    playbackFactor = slowdownFactor;
                    audioTempoFactor = 1d / slowdownFactor;
                    break;
                case "speed_up":
                    var speedupFactor = Random.Shared.NextDouble() * 0.02 + 1.03;
                    playbackFactor = 1d / speedupFactor;
                    audioTempoFactor = speedupFactor;
                    break;
                case "color_correct":
                    var saturation = 1.0 + Random.Shared.NextDouble() * 0.03 + 0.04;
                    var brightness = -(Random.Shared.NextDouble() * 0.03 + 0.04);
                    var gamma = 1.0 - (Random.Shared.NextDouble() * 0.02 + 0.01);
                    videoFilters.Add(FormattableString.Invariant($"eq=saturation={saturation:0.000}:brightness={brightness:0.000}:gamma={gamma:0.000}"));
                    break;
                case "qr_overlay":
                    qrOverlayPath = ResolveQrOverlayPath(inputPath);
                    break;
                case "rotate":
                    var degrees = Random.Shared.NextDouble() * 2d + 3d;
                    var zoom = Random.Shared.NextDouble() * 0.05 + 1.10;
                    var radians = degrees * Math.PI / 180d;
                    videoFilters.Add(FormattableString.Invariant($"scale=iw*{zoom:0.00}:ih*{zoom:0.00}"));
                    videoFilters.Add(FormattableString.Invariant($"rotate={radians:0.0000}:fillcolor=black"));
                    videoFilters.Add(FormattableString.Invariant($"crop=iw/{zoom:0.00}:ih/{zoom:0.00}"));
                    break;
                case "downscale_1080p":
                    if (mediaInfo.Height is > 1080)
                    {
                        videoFilters.Add("scale=-2:1080");
                    }
                    break;
            }
        }

        if (playbackFactor is not null)
        {
            videoFilters.Insert(0, FormattableString.Invariant($"setpts={playbackFactor:0.0000}*PTS"));
        }

        if (audioTempoFactor is not null)
        {
            audioFilters.Insert(0, FormattableString.Invariant($"atempo={audioTempoFactor:0.0000}"));
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
        var minimumRemainingDuration = useLongSlice ? 60d : 30d;

        var slices = new List<(double StartSeconds, double DurationSeconds)>();
        var startSeconds = 0d;
        while (startSeconds < durationSeconds)
        {
            var remainingDuration = durationSeconds - startSeconds;
            if (remainingDuration < minimumRemainingDuration)
            {
                break;
            }

            var sliceDuration = Random.Shared.NextDouble() * (maxSliceDuration - minSliceDuration) + minSliceDuration;
            sliceDuration = Math.Min(sliceDuration, remainingDuration);
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
            candidates.Add(Path.Combine(inputDirectory, "patreon sling.png"));
        }

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "qr.png"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "qr.png"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "patreon sling.png"));

        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDirectory is not null)
        {
            candidates.Add(Path.Combine(currentDirectory.FullName, "video-bot-project", "patreon sling.png"));
            currentDirectory = currentDirectory.Parent;
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "QR overlay image not found. Expected a qr.png or patreon sling.png asset in the repo or application directory.");
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
