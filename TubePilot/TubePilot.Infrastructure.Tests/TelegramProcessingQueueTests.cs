using Microsoft.Extensions.Logging;
using TubePilot.Infrastructure.Telegram;

namespace TubePilot.Infrastructure.Tests;

public sealed class TelegramProcessingQueueTests
{
    [Fact]
    public async Task QueuesOverflowAndTransitionsToProcessingInOrder()
    {
        var queue = CreateQueue(maxConcurrentJobs: 1);
        var events = new List<string>();
        var firstStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanFinish = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = await queue.EnqueueAsync(
            chatId: 42,
            messageId: 1,
            onQueuedAsync: (_, _) => Task.CompletedTask,
            onStartAsync: _ =>
            {
                events.Add("first:start");
                firstStarted.TrySetResult(null);
                return Task.CompletedTask;
            },
            processAsync: async _ =>
            {
                await firstCanFinish.Task;
                events.Add("first:done");
            },
            CancellationToken.None);

        await firstStarted.Task;

        var second = await queue.EnqueueAsync(
            chatId: 42,
            messageId: 2,
            onQueuedAsync: (position, _) =>
            {
                events.Add($"second:queued:{position}");
                return Task.CompletedTask;
            },
            onStartAsync: _ =>
            {
                events.Add("second:start");
                secondStarted.TrySetResult(null);
                return Task.CompletedTask;
            },
            processAsync: _ =>
            {
                events.Add("second:done");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(TelegramProcessingQueue.QueueAdmissionStatus.Started, first.Status);
        Assert.Equal(TelegramProcessingQueue.QueueAdmissionStatus.Queued, second.Status);
        Assert.Equal(1, second.Position);
        Assert.False(secondStarted.Task.IsCompleted);

        firstCanFinish.TrySetResult(null);

        await secondStarted.Task;
        await second.LifecycleTask;

        Assert.Equal(
            new[]
            {
                "first:start",
                "second:queued:1",
                "first:done",
                "second:start",
                "second:done"
            },
            events);
    }

    [Fact]
    public async Task QueuesArePerChat_NotGlobal()
    {
        var queue = CreateQueue(maxConcurrentJobs: 1);
        var firstStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanFinish = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = await queue.EnqueueAsync(
            chatId: 100,
            messageId: 1,
            onQueuedAsync: (_, _) => Task.CompletedTask,
            onStartAsync: _ =>
            {
                firstStarted.TrySetResult(null);
                return Task.CompletedTask;
            },
            processAsync: async _ => await firstCanFinish.Task,
            CancellationToken.None);

        await firstStarted.Task;

        var second = await queue.EnqueueAsync(
            chatId: 200,
            messageId: 1,
            onQueuedAsync: (_, _) => Task.CompletedTask,
            onStartAsync: _ =>
            {
                secondStarted.TrySetResult(null);
                return Task.CompletedTask;
            },
            processAsync: _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(TelegramProcessingQueue.QueueAdmissionStatus.Started, first.Status);
        Assert.Equal(TelegramProcessingQueue.QueueAdmissionStatus.Started, second.Status);
        await secondStarted.Task;

        firstCanFinish.TrySetResult(null);
        await Task.WhenAll(first.LifecycleTask, second.LifecycleTask);
    }

    [Fact]
    public async Task DuplicateMessageInSameChatIsIgnored()
    {
        var queue = CreateQueue(maxConcurrentJobs: 1);
        var started = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = await queue.EnqueueAsync(
            chatId: 1,
            messageId: 99,
            onQueuedAsync: (_, _) => Task.CompletedTask,
            onStartAsync: _ =>
            {
                started.TrySetResult(null);
                return Task.CompletedTask;
            },
            processAsync: async _ => await release.Task,
            CancellationToken.None);

        await started.Task;

        var duplicate = await queue.EnqueueAsync(
            chatId: 1,
            messageId: 99,
            onQueuedAsync: (_, _) => Task.CompletedTask,
            onStartAsync: _ => Task.CompletedTask,
            processAsync: _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(TelegramProcessingQueue.QueueAdmissionStatus.Duplicate, duplicate.Status);
        Assert.True(duplicate.LifecycleTask.IsCompleted);

        release.TrySetResult(null);
        await first.LifecycleTask;
    }

    private static TelegramProcessingQueue CreateQueue(int maxConcurrentJobs)
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.ClearProviders());
        return new TelegramProcessingQueue(maxConcurrentJobs, loggerFactory.CreateLogger<TelegramProcessingQueue>());
    }
}
