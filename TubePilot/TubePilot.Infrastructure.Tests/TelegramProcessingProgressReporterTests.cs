using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Telegram;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramProcessingProgressReporterTests
{
    [Fact]
    public async Task RapidProgressEventsWithinThrottle_ResultInSingleEdit()
    {
        var edits = new List<(DateTimeOffset At, string Text)>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var reporter = CreateReporter(timeProvider, edits, throttleSeconds: 2);

        await reporter.ReportAsync(new VideoProcessingProgress(1, VideoProcessingStage.Transform), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await reporter.ReportAsync(new VideoProcessingProgress(2, VideoProcessingStage.Transform), CancellationToken.None);

        Assert.Single(edits);
    }

    [Fact]
    public async Task AfterThrottleInterval_ReporterEditsPendingUpdate()
    {
        var edits = new List<(DateTimeOffset At, string Text)>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var reporter = CreateReporter(timeProvider, edits, throttleSeconds: 2);

        await reporter.ReportAsync(new VideoProcessingProgress(1, VideoProcessingStage.Transform), CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await reporter.ReportAsync(new VideoProcessingProgress(2, VideoProcessingStage.Transform), CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromSeconds(2.1));
        await reporter.ReportAsync(new VideoProcessingProgress(2, VideoProcessingStage.Transform), CancellationToken.None);

        Assert.Equal(2, edits.Count);
        Assert.True(edits[1].At - edits[0].At >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StageChangeSurfacesInEditedText_ButStillRespectsThrottleInterval()
    {
        var edits = new List<(DateTimeOffset At, string Text)>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var reporter = CreateReporter(timeProvider, edits, throttleSeconds: 2);

        await reporter.ReportAsync(new VideoProcessingProgress(5, VideoProcessingStage.Slicing), CancellationToken.None);
        Assert.Single(edits);
        Assert.Contains("Stage: Slicing", edits[0].Text, StringComparison.Ordinal);

        timeProvider.Advance(TimeSpan.FromSeconds(0.5));
        await reporter.ReportAsync(new VideoProcessingProgress(5, VideoProcessingStage.Transform), CancellationToken.None);
        Assert.Single(edits);

        timeProvider.Advance(TimeSpan.FromSeconds(2.1));
        await reporter.ReportAsync(new VideoProcessingProgress(5, VideoProcessingStage.Transform), CancellationToken.None);

        Assert.Equal(2, edits.Count);
        Assert.True(edits[1].At - edits[0].At >= TimeSpan.FromSeconds(2));
        Assert.Contains("Stage: Transform", edits[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReporterOnlyInvokesEditDelegate_NoSendMessagePathExists()
    {
        var editCalls = 0;
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var reporter = new TelegramProcessingProgressReporter(
            fileName: "file.mp4",
            timeProvider: timeProvider,
            throttleInterval: TimeSpan.FromSeconds(2),
            editMessageText: (_, _) =>
            {
                editCalls++;
                return Task.CompletedTask;
            });

        await reporter.ReportAsync(new VideoProcessingProgress(1, VideoProcessingStage.Init), CancellationToken.None);

        Assert.Equal(1, editCalls);
    }

    private static TelegramProcessingProgressReporter CreateReporter(
        ManualTimeProvider timeProvider,
        List<(DateTimeOffset At, string Text)> edits,
        int throttleSeconds)
        => new(
            fileName: "file.mp4",
            timeProvider: timeProvider,
            throttleInterval: TimeSpan.FromSeconds(throttleSeconds),
            editMessageText: (text, _) =>
            {
                edits.Add((timeProvider.GetUtcNow(), text));
                return Task.CompletedTask;
            });

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
