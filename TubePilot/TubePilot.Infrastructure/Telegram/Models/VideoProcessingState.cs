namespace TubePilot.Infrastructure.Telegram.Models;

internal sealed class VideoProcessingState
{
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public required string LocalPath { get; init; }
    
    public HashSet<string> SelectedOptions { get; } = [];
}
