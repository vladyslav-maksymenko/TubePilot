using Microsoft.Extensions.Logging;
using TubePilot.Core.Contracts;

namespace TubePilot.Infrastructure.Video;

internal sealed class FfmpegVideoProcessor(ILogger<FfmpegVideoProcessor> logger) : IVideoProcessor
{
    public async Task<IReadOnlyList<string>> ProcessAsync(string inputPath, HashSet<string> options, Action<int> progressCallback, CancellationToken ct = default)
    {
        logger.LogInformation("Starting FFmpeg mock processing...");
        
        // Мокаємо реальну роботу (щоб ви могли зараз затестити Telegram бот і його UI)
        for (int i = 0; i <= 100; i += 5)
        {
            progressCallback(i);
            await Task.Delay(200, ct); // Штучна затримка (наче рендеримо відео)
        }
        
        // Створюємо папку "processed" в поточній робочій директоріії, щоб Web-сервер міг її знайти
        var processedDir = Path.Combine(Directory.GetCurrentDirectory(), "processed");
        Directory.CreateDirectory(processedDir);

        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var outPath = Path.Combine(processedDir, $"{baseName}_processed{ext}");
        
        File.Copy(inputPath, outPath, overwrite: true);
        
        progressCallback(100);
        return [outPath];
    }
}
