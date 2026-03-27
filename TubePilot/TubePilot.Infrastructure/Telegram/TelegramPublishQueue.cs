using Microsoft.Extensions.Logging;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramPublishQueue
{
    private readonly ILogger<TelegramPublishQueue> _logger;
    private readonly int _maxConcurrentJobs;
    private readonly object _gate = new();
    private readonly Dictionary<long, ChatQueueState> _chatStates = [];

    public TelegramPublishQueue(int maxConcurrentJobs, ILogger<TelegramPublishQueue> logger)
    {
        _logger = logger;
        _maxConcurrentJobs = Math.Max(1, maxConcurrentJobs);
    }

    public Task<QueueAdmission> EnqueueAsync(
        long chatId,
        int messageId,
        Func<int, CancellationToken, Task> onQueuedAsync,
        Func<CancellationToken, Task> onStartAsync,
        Func<CancellationToken, Task> processAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onQueuedAsync);
        ArgumentNullException.ThrowIfNull(onStartAsync);
        ArgumentNullException.ThrowIfNull(processAsync);

        var entry = new QueueEntry(chatId, messageId, onQueuedAsync, onStartAsync, processAsync, ct);
        var shouldStartImmediately = false;
        var shouldQueue = false;
        var queuedPosition = 0;

        lock (_gate)
        {
            var state = GetChatState(chatId);

            if (!state.MessageIds.Add(messageId))
            {
                return Task.FromResult(QueueAdmission.Duplicate());
            }

            if (state.RunningCount < _maxConcurrentJobs && state.Pending.Count == 0)
            {
                state.RunningCount++;
                shouldStartImmediately = true;
            }
            else
            {
                queuedPosition = state.Pending.Count + 1;
                entry.Position = queuedPosition;
                state.Pending.Enqueue(entry);
                shouldQueue = true;
            }
        }

        if (shouldStartImmediately)
        {
            _ = RunEntryAsync(entry);
            return Task.FromResult(QueueAdmission.Started(entry.Completion.Task));
        }

        return QueueOnBackgroundAsync(entry, queuedPosition, shouldQueue);
    }

    private async Task<QueueAdmission> QueueOnBackgroundAsync(QueueEntry entry, int queuedPosition, bool shouldQueue)
    {
        if (shouldQueue)
        {
            try
            {
                await entry.OnQueuedAsync(queuedPosition, entry.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telegram queued-state update failed for publish job chat {ChatId}, message {MessageId}.", entry.ChatId, entry.MessageId);
            }
        }

        return QueueAdmission.Queued(queuedPosition, entry.Completion.Task);
    }

    private async Task RunEntryAsync(QueueEntry entry)
    {
        try
        {
            try
            {
                await entry.OnStartAsync(entry.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telegram start-state update failed for publish job chat {ChatId}, message {MessageId}.", entry.ChatId, entry.MessageId);
            }

            await entry.ProcessAsync(entry.CancellationToken);
        }
        catch (OperationCanceledException) when (entry.CancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Publish job canceled for chat {ChatId}, message {MessageId}.", entry.ChatId, entry.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publish job failed for chat {ChatId}, message {MessageId}.", entry.ChatId, entry.MessageId);
        }
        finally
        {
            List<QueueEntry> nextEntries = [];

            lock (_gate)
            {
                if (_chatStates.TryGetValue(entry.ChatId, out var state))
                {
                    state.MessageIds.Remove(entry.MessageId);

                    if (state.RunningCount > 0)
                    {
                        state.RunningCount--;
                    }

                    while (state.RunningCount < _maxConcurrentJobs && state.Pending.Count > 0)
                    {
                        var nextEntry = state.Pending.Dequeue();
                        state.RunningCount++;
                        nextEntries.Add(nextEntry);
                    }

                    if (state.RunningCount == 0 && state.Pending.Count == 0 && state.MessageIds.Count == 0)
                    {
                        _chatStates.Remove(entry.ChatId);
                    }
                }
            }

            entry.Completion.TrySetResult(null);

            foreach (var nextEntry in nextEntries)
            {
                _ = RunEntryAsync(nextEntry);
            }
        }
    }

    private ChatQueueState GetChatState(long chatId)
    {
        if (!_chatStates.TryGetValue(chatId, out var state))
        {
            state = new ChatQueueState();
            _chatStates[chatId] = state;
        }

        return state;
    }

    internal sealed record QueueAdmission(QueueAdmissionStatus Status, int Position, Task LifecycleTask)
    {
        public static QueueAdmission Started(Task lifecycleTask) => new(QueueAdmissionStatus.Started, 0, lifecycleTask);

        public static QueueAdmission Queued(int position, Task lifecycleTask) => new(QueueAdmissionStatus.Queued, position, lifecycleTask);

        public static QueueAdmission Duplicate() => new(QueueAdmissionStatus.Duplicate, 0, Task.CompletedTask);
    }

    internal enum QueueAdmissionStatus
    {
        Started,
        Queued,
        Duplicate
    }

    private sealed class ChatQueueState
    {
        public int RunningCount { get; set; }

        public Queue<QueueEntry> Pending { get; } = [];

        public HashSet<int> MessageIds { get; } = [];
    }

    private sealed class QueueEntry(
        long chatId,
        int messageId,
        Func<int, CancellationToken, Task> onQueuedAsync,
        Func<CancellationToken, Task> onStartAsync,
        Func<CancellationToken, Task> processAsync,
        CancellationToken cancellationToken)
    {
        public long ChatId { get; } = chatId;

        public int MessageId { get; } = messageId;

        public Func<int, CancellationToken, Task> OnQueuedAsync { get; } = onQueuedAsync;

        public Func<CancellationToken, Task> OnStartAsync { get; } = onStartAsync;

        public Func<CancellationToken, Task> ProcessAsync { get; } = processAsync;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public int Position { get; set; }

        public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

