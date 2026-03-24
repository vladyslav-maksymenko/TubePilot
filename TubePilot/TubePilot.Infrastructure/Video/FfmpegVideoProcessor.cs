using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive.Options;

namespace TubePilot.Infrastructure.Video;

internal sealed class FfmpegVideoProcessor(IOptionsMonitor<DriveOptions> optionsMonitor, ILogger<FfmpegVideoProcessor> logger) : IVideoProcessor
{
    public async Task<IReadOnlyList<string>> ProcessAsync(string inputPath, HashSet<string> options, Func<int, Task> progressCallback, CancellationToken ct = default)
    {
        logger.LogInformation("Starting FFmpeg mock processing...");
        
        for (int i = 0; i <= 100; i += 5)
        {
            await progressCallback(i);
            await Task.Delay(200, ct);
        }
        
        var processedDir = Path.GetFullPath(optionsMonitor.CurrentValue.ProcessedDirectory);
        Directory.CreateDirectory(processedDir);

        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var outPath = Path.Combine(processedDir, $"{baseName}_processed{ext}");
        
        File.Copy(inputPath, outPath, overwrite: true);
        
        await progressCallback(100);
        return [outPath];
    }
}
