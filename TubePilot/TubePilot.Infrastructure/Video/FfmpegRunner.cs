using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TubePilot.Infrastructure.Video;

internal sealed class FfmpegRunner(ILogger<FfmpegRunner> logger) : IFfmpegRunner
{
    public async Task<FfmpegProbeResult> ProbeAsync(string inputPath, CancellationToken ct = default)
    {
        logger.LogDebug("Running ffprobe for {InputPath}.", inputPath);
        var startInfo = CreateStartInfo("ffprobe");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(inputPath);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ffprobe.");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Unable to start ffprobe. Ensure it is installed and available on PATH.", ex);
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe failed for '{inputPath}': {TrimForMessage(stderr)}");
        }

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var durationSeconds = 0d;

        if (root.TryGetProperty("format", out var formatElement) &&
            formatElement.TryGetProperty("duration", out var durationElement) &&
            double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration))
        {
            durationSeconds = parsedDuration;
        }

        int? width = null;
        int? height = null;
        var hasVideo = false;
        var hasAudio = false;

        if (root.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streamsElement.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecTypeElement))
                {
                    continue;
                }

                var codecType = codecTypeElement.GetString();
                if (codecType == "video")
                {
                    hasVideo = true;
                    if (!width.HasValue && stream.TryGetProperty("width", out var widthElement) && widthElement.TryGetInt32(out var parsedWidth))
                    {
                        width = parsedWidth;
                    }

                    if (!height.HasValue && stream.TryGetProperty("height", out var heightElement) && heightElement.TryGetInt32(out var parsedHeight))
                    {
                        height = parsedHeight;
                    }
                }
                else if (codecType == "audio")
                {
                    hasAudio = true;
                }
            }
        }

        return new FfmpegProbeResult(durationSeconds, width, height, hasVideo, hasAudio);
    }

    public async Task RunAsync(IReadOnlyList<string> arguments, double durationSeconds, Func<int, Task> progressCallback, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(progressCallback);

        logger.LogDebug("Running ffmpeg with {ArgumentCount} argument(s).", arguments.Count);
        var startInfo = CreateStartInfo("ffmpeg");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stderrBuffer = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ffmpeg.");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Unable to start ffmpeg. Ensure it is installed and available on PATH.", ex);
        }

        using var registration = ct.Register(() =>
        {
            TryKill(process);
        });

        var stdoutTask = Task.Run(async () =>
        {
            var lastProgress = -1;
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                var parsedProgress = FfmpegProgressParser.TryParsePercent(line, durationSeconds);
                if (parsedProgress is null || parsedProgress.Value <= lastProgress)
                {
                    continue;
                }

                lastProgress = parsedProgress.Value;
                await progressCallback(parsedProgress.Value);
            }
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                stderrBuffer.Append(buffer, 0, read);
            }
        }, ct);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(ct));

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg failed: {TrimForMessage(stderrBuffer.ToString())}");
            }

            await progressCallback(100);
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cancellation cleanup.
        }
    }

    private static string TrimForMessage(string value, int maxLength = 1500)
        => value.Length <= maxLength ? value : value[..maxLength];
}
