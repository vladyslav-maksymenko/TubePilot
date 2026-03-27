using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramResultThumbnailGenerator(ILogger<TelegramResultThumbnailGenerator> logger) : ITelegramResultThumbnailGenerator
{
    public async Task<string?> TryGenerateAsync(string videoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "video";
        }

        var thumbPath = Path.Combine(Path.GetTempPath(), $"{baseName}_{Guid.NewGuid():N}_thumb.jpg");

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-ss");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-vframes");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add("3");
        startInfo.ArgumentList.Add(thumbPath);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            try
            {
                if (!process.Start())
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Unable to start ffmpeg for thumbnail generation.");
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);
            var stderr = await stderrTask;
            _ = await stdoutTask;

            if (process.ExitCode != 0)
            {
                logger.LogDebug("ffmpeg thumbnail generation failed (ExitCode={ExitCode}): {Stderr}", process.ExitCode, stderr);
                TryDeleteQuietly(thumbPath);
                return null;
            }

            if (!File.Exists(thumbPath))
            {
                return null;
            }

            return thumbPath;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryDeleteQuietly(thumbPath);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Thumbnail generation failed unexpectedly.");
            TryKill(process);
            TryDeleteQuietly(thumbPath);
            return null;
        }
    }

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
            // Best effort.
        }
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
            // Best effort.
        }
    }
}
