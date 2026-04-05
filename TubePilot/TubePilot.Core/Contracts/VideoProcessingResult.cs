namespace TubePilot.Core.Contracts;

public readonly record struct VideoProcessingResult(
    string OutputPath,
    int PartNumber,
    int TotalParts,
    double DurationSeconds,
    long SizeBytes,
    VideoProcessingSummary Summary);
