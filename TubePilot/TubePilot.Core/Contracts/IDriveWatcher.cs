using TubePilot.Core.Domain;

namespace TubePilot.Core.Contracts;

public interface IDriveWatcher
{
    Task<IReadOnlyList<DriveFile>> GetNewFilesAsync(string folderId, CancellationToken ct = default);
    Task<string> DownloadAsync(string fileId, string fileName, string destinationDir, CancellationToken ct = default);
}