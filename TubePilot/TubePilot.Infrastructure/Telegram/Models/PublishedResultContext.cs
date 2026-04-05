using TubePilot.Core.Contracts;

namespace TubePilot.Infrastructure.Telegram.Models;

internal sealed record PublishedResultContext(
    string SourceFileName,
    string ResultFileName,
    string ResultFilePath,
    string? PublicUrl,
    int PartNumber,
    int TotalParts,
    double DurationSeconds,
    long SizeBytes,
    VideoProcessingSummary ProcessingSummary);
