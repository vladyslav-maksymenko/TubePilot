namespace TubePilot.Infrastructure.Telegram;

internal interface IDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken ct);
}

