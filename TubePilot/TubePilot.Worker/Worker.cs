using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive.Options;

namespace TubePilot.Worker;

public class Worker(
    ILogger<Worker> logger, 
    IDriveWatcher driveWatcher, 
    IOptionsMonitor<DriveOptions> optionsMonitor) : BackgroundService
{
    private bool _hasWarnedAboutMissingFolderId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = optionsMonitor.CurrentValue;
            var folderId = options.FolderId ?? string.Empty;
            var destinationDir = options.DownloadDirectory;
            
            if (string.IsNullOrEmpty(folderId))
            {
                if (!_hasWarnedAboutMissingFolderId)
                {
                    logger.LogWarning("GoogleDrive:FolderId is missing in secrets.json! Provide the FolderId to enable polling.");
                    _hasWarnedAboutMissingFolderId = true;
                }
            }
            else
            {
                // Скидаємо прапорець попередження, якщо юзер підкинув ID на льоту.
                _hasWarnedAboutMissingFolderId = false;
                
                logger.LogInformation("Polling Google Drive for new videos...");
                try 
                {
                    var newFiles = await driveWatcher.GetNewFilesAsync(folderId, stoppingToken);
                    foreach(var file in newFiles)
                    {
                        try
                        {
                            var downloadedPath = await driveWatcher.DownloadAsync(file.Id, file.Name, destinationDir, stoppingToken);
                            logger.LogInformation("Successfully processed {FileName} to {Path}", file.Name, downloadedPath);
                        }
                        catch (Exception innerEx)
                        {
                            logger.LogError(innerEx, "Failed to download file {FileName} ({FileId}). Skipping.", file.Name, file.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Critical error while polling Google Drive API.");
                }
            }
            
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}