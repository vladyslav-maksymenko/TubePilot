namespace TubePilot.Core.Contracts;

public interface IGoogleSheetsLogger
{
    Task LogUploadAsync(
        string sourceFile,
        string title,
        string youtubeId,
        string youtubeUrl,
        string status,
        DateTimeOffset? scheduledAtUtc,
        CancellationToken ct = default);
}
