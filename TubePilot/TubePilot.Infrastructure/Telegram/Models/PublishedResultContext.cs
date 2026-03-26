namespace TubePilot.Infrastructure.Telegram.Models;

internal sealed record PublishedResultContext(
    string SourceFileName,
    string ResultFileName,
    string ResultFilePath,
    string? PublicUrl);
