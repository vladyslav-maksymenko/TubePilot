namespace TubePilot.Core.Contracts;

public interface IVideoProcessor
{
    Task<IReadOnlyList<string>> ProcessAsync(string inputPath, HashSet<string> options, Action<int> progressCallback, CancellationToken ct = default);
}