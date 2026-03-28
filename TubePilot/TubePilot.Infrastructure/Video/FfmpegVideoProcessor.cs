using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive.Options;

namespace TubePilot.Infrastructure.Video;

internal sealed class FfmpegVideoProcessor(IOptionsMonitor<DriveOptions> optionsMonitor, ILogger<FfmpegVideoProcessor> logger) : IVideoProcessor
{
    private static readonly HashSet<string> SliceOptions = ["slice", "slice_long"];

    public async Task<IReadOnlyList<string>> ProcessAsync(string inputPath, HashSet<string> options, Func<int, Task> progressCallback, CancellationToken ct = default)
    {
        var processedDir = Path.GetFullPath(optionsMonitor.CurrentValue.ProcessedDirectory);
        Directory.CreateDirectory(processedDir);

        var doSlice = options.Contains("slice");
        var doSliceLong = options.Contains("slice_long");
        var filterOptions = options.Where(o => !SliceOptions.Contains(o)).ToList();

        if (doSlice || doSliceLong)
        {
            var (minDur, maxDur, minLeftover) = doSliceLong
                ? (310.0, 430.0, 60.0)
                : (150.0, 190.0, 30.0);

            await progressCallback(5);
            var slicedParts = await SliceVideoAsync(inputPath, processedDir, minDur, maxDur, minLeftover, ct);

            if (filterOptions.Count == 0)
                return slicedParts;

            var results = new List<string>();
            for (var i = 0; i < slicedParts.Count; i++)
            {
                var partPct = (int)(10 + 90.0 * i / slicedParts.Count);
                await progressCallback(partPct);
                var output = await ApplyCombinedAsync(slicedParts[i], filterOptions, processedDir, ct);
                results.Add(output);
            }
            await progressCallback(100);
            return results;
        }

        if (filterOptions.Count == 0)
            return [];

        await progressCallback(5);
        var duration = await GetDurationAsync(inputPath, ct);
        var result = await ApplyCombinedAsync(inputPath, filterOptions, processedDir, ct, duration, progressCallback);
        await progressCallback(100);
        return [result];
    }

    private async Task<string> ApplyCombinedAsync(
        string inputPath, List<string> options, string processedDir,
        CancellationToken ct, double durationSec = 0, Func<int, Task>? progressCallback = null)
    {
        var vfilters = new List<string>();
        var afilters = new List<string>();
        var suffixParts = new List<string>();
        var rng = Random.Shared;

        foreach (var opt in options)
        {
            switch (opt)
            {
                case "mirror":
                    vfilters.Add("hflip");
                    suffixParts.Add("mir");
                    break;

                case "reduce_audio":
                    var vol = 0.80 + rng.NextDouble() * 0.10; // 0.80-0.90
                    afilters.Add(string.Create(CultureInfo.InvariantCulture, $"volume={vol:F2}"));
                    suffixParts.Add($"vol{(int)(vol * 100)}");
                    break;

                case "slow_down":
                    var slowFactor = 1.04 + rng.NextDouble() * 0.03; // 1.04-1.07
                    vfilters.Insert(0, string.Create(CultureInfo.InvariantCulture, $"setpts={slowFactor:F4}*PTS"));
                    afilters.Insert(0, string.Create(CultureInfo.InvariantCulture, $"atempo={1.0 / slowFactor:F4}"));
                    suffixParts.Add($"slow{(int)(slowFactor * 100)}");
                    break;

                case "speed_up":
                    var speedFactor = 1.03 + rng.NextDouble() * 0.02; // 1.03-1.05
                    vfilters.Insert(0, string.Create(CultureInfo.InvariantCulture, $"setpts={1.0 / speedFactor:F4}*PTS"));
                    afilters.Insert(0, string.Create(CultureInfo.InvariantCulture, $"atempo={speedFactor:F4}"));
                    suffixParts.Add($"fast{(int)(speedFactor * 100)}");
                    break;

                case "color_correct":
                    var sat = 1.0 + 0.04 + rng.NextDouble() * 0.03;    // +4-7%
                    var bright = -(0.04 + rng.NextDouble() * 0.03);     // -4-7%
                    var gamma = 1.0 - (0.01 + rng.NextDouble() * 0.02); // -1-3%
                    vfilters.Add(string.Create(CultureInfo.InvariantCulture, $"eq=saturation={sat:F3}:brightness={bright:F3}:gamma={gamma:F3}"));
                    suffixParts.Add("color");
                    break;

                case "rotate":
                    var degrees = 3.0 + rng.NextDouble() * 2.0; // 3-5°
                    var radians = degrees * Math.PI / 180;
                    var zoom = 1.0 + 0.10 + rng.NextDouble() * 0.05; // 10-15% zoom
                    vfilters.Add(string.Create(CultureInfo.InvariantCulture, $"scale=iw*{zoom:F2}:ih*{zoom:F2}"));
                    vfilters.Add(string.Create(CultureInfo.InvariantCulture, $"rotate={radians:F4}:fillcolor=black"));
                    vfilters.Add(string.Create(CultureInfo.InvariantCulture, $"crop=iw/{zoom:F2}:ih/{zoom:F2}"));
                    suffixParts.Add($"rot{(int)degrees}");
                    break;

                case "downscale_1080p":
                    vfilters.Add("scale=-2:1080");
                    suffixParts.Add("1080p");
                    break;

                case "qr_overlay":
                    // Заглушено — пропускаємо
                    logger.LogDebug("qr_overlay is disabled, skipping");
                    break;
            }
        }

        var suffix = suffixParts.Count > 0 ? string.Join("_", suffixParts) : "processed";
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var outputPath = Path.Combine(processedDir, $"{baseName}_{suffix}{ext}");

        var args = new List<string> { "-i", inputPath };

        var filterParts = new List<string>();
        string finalV = "0:v";
        var needsVideoEncode = false;
        var needsAudioEncode = false;

        if (vfilters.Count > 0)
        {
            filterParts.Add($"[0:v]{string.Join(",", vfilters)}[vout]");
            finalV = "vout";
            needsVideoEncode = true;
        }

        if (afilters.Count > 0)
        {
            filterParts.Add($"[0:a]{string.Join(",", afilters)}[aout]");
            needsAudioEncode = true;
        }

        if (filterParts.Count > 0)
        {
            args.AddRange(["-filter_complex", string.Join(";", filterParts)]);
            args.AddRange(["-map", $"[{finalV}]"]);

            if (needsVideoEncode)
            {
                var bitrate = options.Contains("downscale_1080p") ? "10000k" : "35000k";
                args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-b:v", bitrate, "-threads", "0"]);
            }
            else
            {
                args.AddRange(["-c:v", "copy"]);
            }

            var hasAudio = await HasAudioStreamAsync(inputPath, ct);
            if (hasAudio)
            {
                if (needsAudioEncode)
                {
                    args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "192k"]);
                }
                else
                {
                    args.AddRange(["-map", "0:a", "-c:a", "copy"]);
                }
            }
        }
        else
        {
            args.AddRange(["-c", "copy"]);
        }

        args.Add(outputPath);

        await RunFfmpegAsync(args, durationSec, progressCallback, ct);
        return outputPath;
    }

    private async Task<List<string>> SliceVideoAsync(
        string inputPath, string processedDir,
        double minDur, double maxDur, double minLeftover,
        CancellationToken ct)
    {
        var duration = await GetDurationAsync(inputPath, ct);
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var outputs = new List<string>();
        var rng = Random.Shared;

        var start = 0.0;
        var partNum = 1;

        while (start < duration)
        {
            var sliceDur = minDur + rng.NextDouble() * (maxDur - minDur);
            var remaining = duration - start;
            if (remaining < minLeftover) break;
            sliceDur = Math.Min(sliceDur, remaining);

            var outputPath = Path.Combine(processedDir, $"{baseName}_part{partNum:D2}{ext}");
            var args = new List<string>
            {
                "-i", inputPath,
                "-ss", start.ToString("F2", CultureInfo.InvariantCulture),
                "-t", sliceDur.ToString("F2", CultureInfo.InvariantCulture),
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                outputPath
            };

            await RunFfmpegAsync(args, 0, null, ct);
            outputs.Add(outputPath);

            start += sliceDur;
            partNum++;
        }

        logger.LogInformation("Sliced {InputFile} into {Count} parts", Path.GetFileName(inputPath), outputs.Count);
        return outputs;
    }

    private async Task<double> GetDurationAsync(string inputPath, CancellationToken ct)
    {
        var info = await ProbeAsync(inputPath, ct);
        var durationStr = info.RootElement.GetProperty("format").GetProperty("duration").GetString();
        return double.Parse(durationStr!, CultureInfo.InvariantCulture);
    }

    private async Task<bool> HasAudioStreamAsync(string inputPath, CancellationToken ct)
    {
        var info = await ProbeAsync(inputPath, ct);
        if (!info.RootElement.TryGetProperty("streams", out var streams))
            return false;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.TryGetProperty("codec_type", out var codecType) && codecType.GetString() == "audio")
                return true;
        }
        return false;
    }

    private async Task<JsonDocument> ProbeAsync(string inputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(inputPath);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffprobe");
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed with exit code {proc.ExitCode}");

        return JsonDocument.Parse(output);
    }

    private async Task RunFfmpegAsync(List<string> args, double durationSec, Func<int, Task>? progressCallback, CancellationToken ct)
    {
        var useProgress = durationSec > 0 && progressCallback != null;
        var allArgs = new List<string> { "-y", "-hide_banner", "-loglevel", "warning" };
        if (useProgress)
            allArgs.AddRange(["-progress", "pipe:1"]);
        allArgs.AddRange(args);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in allArgs)
            psi.ArgumentList.Add(arg);

        logger.LogInformation("FFmpeg: ffmpeg {Args}", string.Join(" ", allArgs));

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");

        if (useProgress)
        {
            var lastPct = -1;
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (line.StartsWith("out_time_us=") && double.TryParse(line.AsSpan("out_time_us=".Length), CultureInfo.InvariantCulture, out var us))
                {
                    var pct = Math.Min((int)(us / 1_000_000 / durationSec * 100), 99);
                    if (pct >= lastPct + 10)
                    {
                        lastPct = pct;
                        await progressCallback!(pct);
                    }
                }
            }
        }

        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            logger.LogError("FFmpeg failed: {Stderr}", stderr);
            throw new InvalidOperationException($"FFmpeg failed (exit {proc.ExitCode}): {stderr[..Math.Min(stderr.Length, 500)]}");
        }
    }
}
