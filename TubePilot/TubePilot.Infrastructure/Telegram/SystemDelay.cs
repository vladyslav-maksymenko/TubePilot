namespace TubePilot.Infrastructure.Telegram;

internal sealed class SystemDelay : IDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}

