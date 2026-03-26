namespace TubePilot.Core.Contracts;

public enum VideoProcessingStage
{
    Init,
    Slicing,
    Transform,
    Finalizing
}

public readonly record struct VideoProcessingProgress(int Percent, VideoProcessingStage Stage);

