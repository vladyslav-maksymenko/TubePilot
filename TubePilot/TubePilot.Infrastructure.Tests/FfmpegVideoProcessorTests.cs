using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Drive.Options;
using TubePilot.Infrastructure.Video;

namespace TubePilot.Infrastructure.Tests;

public sealed class FfmpegVideoProcessorTests
{
    [Fact]
    public async Task ProcessAsync_CombinesMultipleFiltersInOnePass()
    {
        var inputPath = CreateInputFile();
        var processedDir = CreateTempDirectory();
        var runner = new RecordingFfmpegRunner(
            new FfmpegProbeResult(120d, 1920, 1080, HasVideo: true, HasAudio: true));
        var processor = CreateProcessor(processedDir, runner);
        var progressUpdates = new List<VideoProcessingProgress>();
        var options = new HashSet<string> { "mirror", "reduce_audio", "color_correct" };

        var outputs = await processor.ProcessAsync(inputPath, options, progress =>
        {
            progressUpdates.Add(progress);
            return Task.CompletedTask;
        });

        Assert.Single(outputs);
        Assert.True(File.Exists(outputs[0].OutputPath));
        Assert.Single(runner.RunCalls);
        Assert.Contains("-filter_complex", runner.RunCalls[0].Arguments);
        Assert.Contains("hflip", string.Join(' ', runner.RunCalls[0].Arguments));
        Assert.Contains("volume=", string.Join(' ', runner.RunCalls[0].Arguments));
        Assert.Contains("eq=saturation=", string.Join(' ', runner.RunCalls[0].Arguments));
        Assert.Contains("-c:v", runner.RunCalls[0].Arguments);
        Assert.Contains("libx264", runner.RunCalls[0].Arguments);
        Assert.Contains("-c:a", runner.RunCalls[0].Arguments);
        Assert.Contains("aac", runner.RunCalls[0].Arguments);
        Assert.Contains(progressUpdates, progress => progress.Percent == 100);
        Assert.Contains(progressUpdates, progress => progress.Stage == VideoProcessingStage.Transform);
        Assert.Contains(progressUpdates, progress => progress.Stage == VideoProcessingStage.Finalizing);
    }

    [Fact]
    public async Task ProcessAsync_SlicesThenTransformsEachSegment()
    {
        var inputPath = CreateInputFile();
        var processedDir = CreateTempDirectory();
        var runner = new RecordingFfmpegRunner(
            new FfmpegProbeResult(170d, 1920, 1440, HasVideo: true, HasAudio: true));
        var processor = CreateProcessor(processedDir, runner);
        var options = new HashSet<string> { "slice", "mirror" };

        var progressUpdates = new List<VideoProcessingProgress>();
        var outputs = await processor.ProcessAsync(inputPath, options, progress =>
        {
            progressUpdates.Add(progress);
            return Task.CompletedTask;
        });

        Assert.Single(outputs);
        Assert.True(File.Exists(outputs[0].OutputPath));
        Assert.Equal(2, runner.RunCalls.Count);
        Assert.Contains("-avoid_negative_ts", runner.RunCalls[0].Arguments);
        Assert.Contains("make_zero", runner.RunCalls[0].Arguments);
        Assert.Contains("-filter_complex", runner.RunCalls[1].Arguments);
        Assert.Contains("hflip", string.Join(' ', runner.RunCalls[1].Arguments));
        Assert.Contains(progressUpdates, progress => progress.Stage == VideoProcessingStage.Slicing);
        Assert.Contains(progressUpdates, progress => progress.Stage == VideoProcessingStage.Transform);
        Assert.Contains(progressUpdates, progress => progress.Stage == VideoProcessingStage.Finalizing);
    }

    [Fact]
    public async Task ProcessAsync_MissingInputThrowsBeforeProbe()
    {
        var processedDir = CreateTempDirectory();
        var runner = new RecordingFfmpegRunner(
            new FfmpegProbeResult(120d, 1920, 1080, HasVideo: true, HasAudio: true));
        var processor = CreateProcessor(processedDir, runner);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            processor.ProcessAsync(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4"), new HashSet<string> { "mirror" }, _ => Task.CompletedTask));

        Assert.Empty(runner.RunCalls);
    }

    [Fact]
    public async Task ProcessAsync_NoVideoStreamThrows()
    {
        var inputPath = CreateInputFile();
        var processedDir = CreateTempDirectory();
        var runner = new RecordingFfmpegRunner(
            new FfmpegProbeResult(120d, null, null, HasVideo: false, HasAudio: true));
        var processor = CreateProcessor(processedDir, runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(inputPath, new HashSet<string> { "mirror" }, _ => Task.CompletedTask));

        Assert.Empty(runner.RunCalls);
    }

    [Fact]
    public async Task ProcessAsync_RunnerFailureBubblesHelpfulMessage()
    {
        var inputPath = CreateInputFile();
        var processedDir = CreateTempDirectory();
        var runner = new RecordingFfmpegRunner(
            new FfmpegProbeResult(120d, 1920, 1080, HasVideo: true, HasAudio: true))
        {
            FailureMessage = "simulated stderr line"
        };
        var processor = CreateProcessor(processedDir, runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(inputPath, new HashSet<string> { "mirror" }, _ => Task.CompletedTask));

        Assert.Contains("simulated stderr line", exception.Message);
    }

    private static FfmpegVideoProcessor CreateProcessor(string processedDir, RecordingFfmpegRunner runner)
        => new(new TestOptionsMonitor<DriveOptions>(new DriveOptions { ProcessedDirectory = processedDir }), runner, NullLogger<FfmpegVideoProcessor>.Instance);

    private static string CreateInputFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TubePilot_{Guid.NewGuid():N}.mp4");
        File.WriteAllText(path, "placeholder");
        return path;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TubePilot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

internal sealed class RecordingFfmpegRunner(FfmpegProbeResult probeResult) : IFfmpegRunner
{
    public List<RecordedRun> RunCalls { get; } = [];

    public string? FailureMessage { get; init; }

    public Task<FfmpegProbeResult> ProbeAsync(string inputPath, CancellationToken ct = default)
        => Task.FromResult(probeResult);

    public async Task RunAsync(IReadOnlyList<string> arguments, double durationSeconds, Func<int, Task> progressCallback, CancellationToken ct = default)
    {
        RunCalls.Add(new RecordedRun(arguments.ToArray(), durationSeconds));

        if (!string.IsNullOrWhiteSpace(FailureMessage))
        {
            throw new InvalidOperationException($"ffmpeg failed: {FailureMessage}");
        }

        await progressCallback(0);
        await progressCallback(48);
        await progressCallback(100);

        var outputPath = arguments[^1];
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, "rendered", ct);
    }
}

internal sealed record RecordedRun(string[] Arguments, double DurationSeconds);

internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;

    public T Value => value;

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
