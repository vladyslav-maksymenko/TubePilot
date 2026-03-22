using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using TubePilot.Core.Contracts;
using TubePilot.Core.Domain;
using TubePilot.Core.Utils;
using TubePilot.Infrastructure.Drive.Options;
using TubePilot.Infrastructure.Drive.State;

namespace TubePilot.Infrastructure.Drive;

internal sealed class GoogleDriveWatcher : IDriveWatcher
{
    private readonly DriveService _driveService;
    private readonly IKnownFilesStore _knownFilesStore;
    private readonly ILogger<GoogleDriveWatcher> _logger;

    public GoogleDriveWatcher(DriveOptions options, IKnownFilesStore knownFilesStore, ILogger<GoogleDriveWatcher> logger)
    {
        _logger = logger;
        _knownFilesStore = knownFilesStore;
        
        var absolutePath = Path.GetFullPath(options.DownloadDirectory);
        _logger.LogInformation($"GoogleDriveWatcher initialized. Default output directory: {absolutePath}");
        
        _driveService = CreateDriveService(options);
    }

    public async Task<IReadOnlyList<DriveFile>> GetNewFilesAsync(string folderId, CancellationToken ct = default)
    {
        _logger.LogDebug($"Polling Drive folder {folderId}");
        
        var request = _driveService.Files.List();
        request.Q = $"'{folderId}' in parents and mimeType contains 'video/' and trashed = false";
        request.Fields = "files(id, name, mimeType, size, createdTime)";
        request.OrderBy = "createdTime desc";

        var response = await request.ExecuteAsync(ct);

        if (response.Files is null || response.Files.Count == 0)
        {
            _logger.LogDebug($"No files found in folder {folderId}");
            return [];
        }
        
        var newFiles = response.Files
            .Where(f => !_knownFilesStore.Contains(f.Id))
            .Where(f => f.Size > 0)
            .Select(f => new DriveFile
            {
                Id = f.Id,
                Name = f.Name,
                MimeType = f.MimeType,
                SizeBytes = f.Size,
                CreatedAt = f.CreatedTimeDateTimeOffset ?? DateTimeOffset.UtcNow,
            })
            .ToArray();

        _logger.LogInformation($"Found {newFiles.Count()} new file(s) in folder {folderId}");

        return newFiles;
    }

    public async Task<string> DownloadAsync(string fileId, string fileName, string destinationDir, CancellationToken ct = default)
    {
        var safeFileName = FileNameSanitizer.Sanitize(fileName);
        Directory.CreateDirectory(destinationDir);
        var localPath = Path.Combine(destinationDir, safeFileName);

        if (File.Exists(localPath))
        {
            _logger.LogWarning($"File {safeFileName} already exists locally, marking as processed");
            _knownFilesStore.Add(fileId);
            return localPath;
        }

        _logger.LogInformation($"Downloading {safeFileName} ({fileId})...");

        var downloadRequest = _driveService.Files.Get(fileId);
        
        try
        {
            await using (var outputStream = File.Create(localPath))
            {
                downloadRequest.MediaDownloader.ProgressChanged += progress =>
                {
                    if (progress.Status == Google.Apis.Download.DownloadStatus.Completed)
                    {
                        _logger.LogInformation($"Download complete: {safeFileName}");
                    }
                    else if (progress.Status == Google.Apis.Download.DownloadStatus.Failed)
                    {
                        _logger.LogError($"Download failed: {safeFileName}");
                    }
                };

                var result = await downloadRequest.DownloadAsync(outputStream, ct);

                if (result.Status == Google.Apis.Download.DownloadStatus.Failed)
                {
                    throw new IOException($"Failed to download file {safeFileName}: {result.Exception?.Message}");
                }
            }
            _knownFilesStore.Add(fileId);
            _logger.LogInformation($"Saved to {localPath}");
            
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Download aborted. Cleaning up corrupted file {localPath}");
            
            // Якщо обірвався інтернет або Google кинув помилку - видаляємо "битий" файл 0 байт.
            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }
            throw;
        }
    }

    private static DriveService CreateDriveService(DriveOptions options)
    {
        if (options.ServiceAccount is null || string.IsNullOrWhiteSpace(options.ServiceAccount.ClientEmail))
        {
            throw new InvalidOperationException("ServiceAccount configuration is missing. Please provide valid credentials in secrets.json.");
        }
        var credential = new ServiceAccountCredential(new ServiceAccountCredential.Initializer(options.ServiceAccount.ClientEmail)
            {
                Scopes = [DriveService.Scope.DriveReadonly]
            }.FromPrivateKey(options.ServiceAccount.PrivateKey));

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            // ApplicationName використовується серверами Google лише для аналітики та збору логів (як User-Agent).
            // Воно не впливає на авторизацію і не повинно в обов'зковому порядку збігатися з назвою проєкту в Google Cloud.
            ApplicationName = "TubePilot",
        });
    }
}