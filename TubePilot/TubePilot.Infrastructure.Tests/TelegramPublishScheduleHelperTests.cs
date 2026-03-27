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

    [Fact]
    public void GetNextFreeSlotUtc_UsesToday_WhenDailyTimeInFuture()
    {
        var next = PublishingScheduleHelper.GetNextFreeSlotUtc(
            utcNow: DateTimeOffset.Parse("2026-01-15T07:00:00+00:00"), // 09:00 local
            lastScheduledAtUtc: null,
            timeZoneId: "Europe/Kiev",
            dailyPublishTime: "10:00");

        Assert.Equal(DateTimeOffset.Parse("2026-01-15T08:00:00+00:00"), next);
    }

    [Fact]
    public void GetNextFreeSlotUtc_UsesTomorrow_WhenDailyTimeAlreadyPassed()
    {
        var next = PublishingScheduleHelper.GetNextFreeSlotUtc(
            utcNow: DateTimeOffset.Parse("2026-01-15T09:30:00+00:00"), // 11:30 local
            lastScheduledAtUtc: null,
            timeZoneId: "Europe/Kiev",
            dailyPublishTime: "10:00");

        Assert.Equal(DateTimeOffset.Parse("2026-01-16T08:00:00+00:00"), next);
    }

    [Fact]
    public void GetNextFreeSlotUtc_UsesDayAfterLastScheduled()
    {
        var next = PublishingScheduleHelper.GetNextFreeSlotUtc(
            utcNow: DateTimeOffset.Parse("2026-01-15T06:00:00+00:00"),
            lastScheduledAtUtc: DateTimeOffset.Parse("2026-01-15T08:00:00+00:00"),
            timeZoneId: "Europe/Kiev",
            dailyPublishTime: "10:00");

        Assert.Equal(DateTimeOffset.Parse("2026-01-16T08:00:00+00:00"), next);
    }

    [Fact]
    public void AddLocalDays_PreservesLocalTime()
    {
        var next = PublishingScheduleHelper.AddLocalDays(
            utcTime: DateTimeOffset.Parse("2026-01-15T08:00:00+00:00"),
            days: 2,
            timeZoneId: "Europe/Kiev");

        Assert.Equal(DateTimeOffset.Parse("2026-01-17T08:00:00+00:00"), next);
    }
}
