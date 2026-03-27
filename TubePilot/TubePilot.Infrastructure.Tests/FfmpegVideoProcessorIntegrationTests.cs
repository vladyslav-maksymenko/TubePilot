using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure;

namespace TubePilot.Infrastructure.Tests;

public sealed class FfmpegVideoProcessorIntegrationTests
{
    [Fact]
    public async Task HappyPath_MirrorProducesTransformedOutput()
    {
        var workDir = CreateTempDirectory();
        var processedDir = Path.Combine(workDir, "processed");
        Directory.CreateDirectory(processedDir);
        var inputPath = Path.Combine(workDir, "input.mp4");
        await GenerateVideoAsync(inputPath);

        using var provider = BuildProvider(processedDir);
        var processor = provider.GetRequiredService<IVideoProcessor>();
        var progressUpdates = new List<VideoProcessingProgress>();

        var outputs = await processor.ProcessAsync(inputPath, new HashSet<string> { "mirror" }, progress =>
        {
            progressUpdates.Add(progress);
            return Task.CompletedTask;
        });

        Assert.Single(outputs);
        Assert.True(File.Exists(outputs[0].OutputPath));
        Assert.Contains(progressUpdates, progress => progress.Percent == 100);
        Assert.Contains(progressUpdates, progress => progress.Stage == VideoProcessingStage.Transform);
        await AssertDifferentHashesAsync(inputPath, outputs[0].OutputPath);
    }

    [Fact]
    public async Task MissingInput_ThrowsFileNotFoundException()
    {
        var workDir = CreateTempDirectory();
        var processedDir = Path.Combine(workDir, "processed");
        Directory.CreateDirectory(processedDir);

        using var provider = BuildProvider(processedDir);
        var processor = provider.GetRequiredService<IVideoProcessor>();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            processor.ProcessAsync(Path.Combine(workDir, "missing.mp4"), new HashSet<string> { "mirror" }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task InvalidProcessedDirectory_Throws()
    {
        var workDir = CreateTempDirectory();
        var processedPath = Path.Combine(workDir, "processed-as-file");
        await File.WriteAllTextAsync(processedPath, "not a directory");
        var inputPath = Path.Combine(workDir, "input.mp4");
        await GenerateVideoAsync(inputPath);

        using var provider = BuildProvider(processedPath);
        var processor = provider.GetRequiredService<IVideoProcessor>();

        await Assert.ThrowsAnyAsync<IOException>(() =>
            processor.ProcessAsync(inputPath, new HashSet<string> { "mirror" }, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task AudioOnlyInput_ThrowsNoVideoStream()
    {
        var workDir = CreateTempDirectory();
        var processedDir = Path.Combine(workDir, "processed");
        Directory.CreateDirectory(processedDir);
        var audioPath = Path.Combine(workDir, "audio.m4a");
        await GenerateAudioOnlyAsync(audioPath);

        using var provider = BuildProvider(processedDir);
        var processor = provider.GetRequiredService<IVideoProcessor>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(audioPath, new HashSet<string> { "mirror" }, _ => Task.CompletedTask));

        Assert.Contains("does not contain a video stream", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommaDecimalCulture_ReduceAudioStillSucceeds()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var locale = CultureInfo.GetCultureInfo("uk-UA");

        try
        {
            CultureInfo.CurrentCulture = locale;
            CultureInfo.CurrentUICulture = locale;

            var workDir = CreateTempDirectory();
            var processedDir = Path.Combine(workDir, "processed");
            Directory.CreateDirectory(processedDir);
            var inputPath = Path.Combine(workDir, "input.mp4");
            await GenerateVideoAsync(inputPath);

            using var provider = BuildProvider(processedDir);
            var processor = provider.GetRequiredService<IVideoProcessor>();

        var outputs = await processor.ProcessAsync(inputPath, new HashSet<string> { "reduce_audio" }, _ => Task.CompletedTask);

        Assert.Single(outputs);
        Assert.True(File.Exists(outputs[0].OutputPath));
        await AssertDifferentHashesAsync(inputPath, outputs[0].OutputPath);
    }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static ServiceProvider BuildProvider(string processedDir)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GoogleDrive:ProcessedDirectory"] = processedDir
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TubePilot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task GenerateVideoAsync(string outputPath)
    {
        await RunFfmpegAsync([
            "-y",
            "-f", "lavfi",
            "-i", "testsrc=duration=4:size=1280x720:rate=30",
            "-f", "lavfi",
            "-i", "sine=frequency=1000:duration=4",
            "-shortest",
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            outputPath
        ]);
    }

    private static async Task GenerateAudioOnlyAsync(string outputPath)
    {
        await RunFfmpegAsync([
            "-y",
            "-f", "lavfi",
            "-i", "sine=frequency=1000:duration=3",
            "-c:a", "aac",
            outputPath
        ]);
    }

    private static async Task AssertDifferentHashesAsync(string firstPath, string secondPath)
    {
        var firstHash = await GetSha256Async(firstPath);
        var secondHash = await GetSha256Async(secondPath);
        Assert.NotEqual(firstHash, secondHash);
    }

    private static async Task<string> GetSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task RunFfmpegAsync(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start ffmpeg.");
        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed: {stderr}\n{stdout}");
        }
    }
}
