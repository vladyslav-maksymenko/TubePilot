using TubePilot.Infrastructure.Telegram;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramPublishScheduleHelperTests
{
    [Fact]
    public void GetDefaultTitle_StripsExtension()
    {
        var title = PublishingScheduleHelper.GetDefaultTitle("My.Video.Final.mp4");

        Assert.Equal("My.Video.Final", title);
    }

    [Fact]
    public void TryParseScheduledPublishAt_ConvertsLocalTimeToUtc()
    {
        var ok = PublishingScheduleHelper.TryParseScheduledPublishAt(
            "2026-01-15 12:30",
            "Europe/Kiev",
            new DateTimeOffset(2026, 01, 15, 07, 00, 00, TimeSpan.Zero),
            out var scheduledUtc,
            out var errorMessage);

        Assert.True(ok);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T10:30:00+00:00"), scheduledUtc);
        Assert.Equal(string.Empty, errorMessage);
    }

    [Fact]
    public void TryParseScheduledPublishAt_RejectsPastTimes()
    {
        var ok = PublishingScheduleHelper.TryParseScheduledPublishAt(
            "2026-01-15 12:30",
            "Europe/Kiev",
            new DateTimeOffset(2026, 01, 15, 10, 29, 30, TimeSpan.Zero),
            out var scheduledUtc,
            out var errorMessage);

        Assert.False(ok);
        Assert.Equal(default, scheduledUtc);
        Assert.NotEmpty(errorMessage);
    }

    [Fact]
    public void NormalizeTags_TrimsAndDeduplicates()
    {
        var tags = PublishingScheduleHelper.NormalizeTags("alpha, beta, Alpha, gamma");

        Assert.Equal("alpha, beta, gamma", tags);
    }
}
