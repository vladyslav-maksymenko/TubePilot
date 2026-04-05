using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive.Options;

namespace TubePilot.Worker;

public class Worker(
    ILogger<Worker> logger, 
    IDriveWatcher driveWatcher, 
    ITelegramBotService telegramBot,
    IChannelStore channelStore,
    IOptionsMonitor<DriveOptions> optionsMonitor) : BackgroundService
{
    private bool _hasWarnedAboutNoFolders;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = optionsMonitor.CurrentValue;
            var destinationDir = options.DownloadDirectory;

            // Collect all folder IDs to poll: from channel store + legacy config
            var folderIds = new List<string>();

            var storeChannels = channelStore.GetAllChannelsWithFolders();
            foreach (var ch in storeChannels)
            {
                if (!string.IsNullOrWhiteSpace(ch.DriveFolderId))
                    folderIds.Add(ch.DriveFolderId);
            }

            // Legacy: single FolderId from config (backward compat)
            var legacyFolderId = options.FolderId;
            if (!string.IsNullOrWhiteSpace(legacyFolderId) && !folderIds.Contains(legacyFolderId))
            {
                folderIds.Add(legacyFolderId);
            }

            if (folderIds.Count == 0)
            {
                if (!_hasWarnedAboutNoFolders)
                {
                    logger.LogWarning("No Drive folders configured. Add channels via /channels or set GoogleDrive:FolderId in secrets.json.");
                    _hasWarnedAboutNoFolders = true;
                }
            }
            else
            {
                _hasWarnedAboutNoFolders = false;

                foreach (var folderId in folderIds)
                {
                    logger.LogInformation("Polling Google Drive folder {FolderId}...", folderId);
                    try
                    {
                        var newFiles = await driveWatcher.GetNewFilesAsync(folderId, stoppingToken);
                        foreach (var file in newFiles)
                        {
                            try
                            {
                                var downloadedPath = await driveWatcher.DownloadAsync(file.Id, file.Name, destinationDir, stoppingToken);
                                logger.LogInformation("Successfully downloaded {FileName} to {Path}", file.Name, downloadedPath);
                                await telegramBot.NotifyNewVideoAsync(file, downloadedPath, stoppingToken);
                            }
                            catch (Exception innerEx)
                            {
                                logger.LogError(innerEx, "Failed to download file {FileName} ({FileId}). Skipping.", file.Name, file.Id);
                            }
                        }
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Transient error polling Drive folder {FolderId}. Will retry next cycle.", folderId);
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollingIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
