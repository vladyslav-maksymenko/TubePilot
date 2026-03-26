using Microsoft.Extensions.Logging;
using TubePilot.Infrastructure.Telegram;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramQueueUxProofTests
{
    [Fact]
    public async Task WhenConcurrencyIsOne_SecondJobGetsQueuedTextThenLaterProcessingText()
    {
        var queue = CreateQueue(maxConcurrentJobs: 1);
        var edits = new List<string>();
        var firstStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstFinish = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstState = new State("file_one.mp4");
        var secondState = new State("file_two.mp4");

        var first = await queue.EnqueueAsync(
            chatId: 1,
            messageId: 10,
            onQueuedAsync: (_, _) => Task.CompletedTask,
            onStartAsync: _ =>
            {
                edits.Add(TelegramProcessingMessageTemplates.BuildProcessingStartText(firstState.FileName));
                firstStarted.TrySetResult(null);
                return Task.CompletedTask;
            },
            processAsync: async _ => await allowFirstFinish.Task,
            CancellationToken.None);

        await firstStarted.Task;

        var second = await queue.EnqueueAsync(
            chatId: 1,
            messageId: 11,
            onQueuedAsync: (position, _) =>
            {
                edits.Add(TelegramProcessingMessageTemplates.BuildQueuedStatusText(secondState.FileName, position));
                return Task.CompletedTask;
            },
            onStartAsync: _ =>
            {
                edits.Add(TelegramProcessingMessageTemplates.BuildProcessingStartText(secondState.FileName));
                secondStarted.TrySetResult(null);
                return Task.CompletedTask;
            },
            processAsync: _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(TelegramProcessingQueue.QueueAdmissionStatus.Started, first.Status);
        Assert.Equal(TelegramProcessingQueue.QueueAdmissionStatus.Queued, second.Status);
        Assert.Equal(1, second.Position);

        Assert.Contains("Queued (#1)", edits[1], StringComparison.Ordinal);

        allowFirstFinish.TrySetResult(null);

        await secondStarted.Task;
        await Task.WhenAll(first.LifecycleTask, second.LifecycleTask);

        Assert.Contains("GPU ОБРОБКА: ЧЕРГА", edits[1], StringComparison.Ordinal);
        Assert.Contains("GPU ОБРОБКА: АКТИВНО", edits[2], StringComparison.Ordinal);
        Assert.Contains("[----------] 0%", edits[2], StringComparison.Ordinal);
    }

    private static TelegramProcessingQueue CreateQueue(int maxConcurrentJobs)
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.ClearProviders());
        return new TelegramProcessingQueue(maxConcurrentJobs, loggerFactory.CreateLogger<TelegramProcessingQueue>());
    }

    private sealed record State(string FileName);
}

