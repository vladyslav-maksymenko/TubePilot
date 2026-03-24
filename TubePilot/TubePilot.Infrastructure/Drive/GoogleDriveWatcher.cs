using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    public GoogleDriveWatcher(IOptions<DriveOptions> options, IKnownFilesStore knownFilesStore, ILogger<GoogleDriveWatcher> logger)
    {
        _logger = logger;
        _knownFilesStore = knownFilesStore;

        var opts = options.Value;
        var absolutePath = Path.GetFullPath(opts.DownloadDirectory);
        _logger.LogInformation("GoogleDriveWatcher initialized. Default output directory: {OutputDir}", absolutePath);
        
        _driveService = CreateDriveService(opts);
    }

    public async Task<IReadOnlyList<DriveFile>> GetNewFilesAsync(string folderId, CancellationToken ct = default)
    {
        _logger.LogDebug("Polling Drive folder {FolderId}", folderId);
        
        var request = _driveService.Files.List();
        request.Q = $"'{folderId}' in parents and mimeType contains 'video/' and trashed = false";
        request.Fields = "files(id, name, mimeType, size, createdTime)";
        request.OrderBy = "createdTime desc";

        var response = await request.ExecuteAsync(ct);

        if (response.Files is null || response.Files.Count == 0)
        {
            _logger.LogDebug("No files found in folder {FolderId}", folderId);
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

        _logger.LogInformation("Found {Count} new file(s) in folder {FolderId}", newFiles.Length, folderId);

        return newFiles;
    }

    public async Task<string> DownloadAsync(string fileId, string fileName, string destinationDir, CancellationToken ct = default)
    {
        var safeFileName = FileNameSanitizer.Sanitize(fileName);
        Directory.CreateDirectory(destinationDir);
        var localPath = Path.Combine(destinationDir, safeFileName);

        if (File.Exists(localPath))
        {
            _logger.LogWarning("File {FileName} already exists locally, marking as processed", safeFileName);
            _knownFilesStore.Add(fileId);
            return localPath;
        }

        _logger.LogInformation("Downloading {FileName} ({FileId})...", safeFileName, fileId);

        var downloadRequest = _driveService.Files.Get(fileId);
        
        try
        {
            await using (var outputStream = File.Create(localPath))
            {
                downloadRequest.MediaDownloader.ProgressChanged += progress =>
                {
                    if (progress.Status == Google.Apis.Download.DownloadStatus.Completed)
                    {
                        _logger.LogInformation("Download complete: {FileName}", safeFileName);
                    }
                    else if (progress.Status == Google.Apis.Download.DownloadStatus.Failed)
                    {
                        _logger.LogError("Download failed: {FileName}", safeFileName);
                    }
                };

                var result = await downloadRequest.DownloadAsync(outputStream, ct);

                if (result.Status == Google.Apis.Download.DownloadStatus.Failed)
                {
                    throw new IOException($"Failed to download file {safeFileName}: {result.Exception?.Message}");
                }
            }
            _knownFilesStore.Add(fileId);
            _logger.LogInformation("Saved to {LocalPath}", localPath);
            
            return localPath;
        }
        catch
        {
            _logger.LogWarning("Download aborted. Cleaning up corrupted file {LocalPath}", localPath);
            
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