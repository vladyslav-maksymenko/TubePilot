namespace TubePilot.Core.Contracts;

public readonly record struct VideoProcessingProgress(int Percent, VideoProcessingStage Stage);
