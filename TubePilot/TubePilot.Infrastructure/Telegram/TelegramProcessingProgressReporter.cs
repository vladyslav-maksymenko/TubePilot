using System.Net;
using TubePilot.Core.Contracts;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramProcessingProgressReporter(
    string fileName,
    TimeProvider timeProvider,
    TimeSpan throttleInterval,
    Func<string, CancellationToken, Task> editMessageText,
    Func<VideoProcessingStage, string>? formatStage = null)
{
    private readonly string _fileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TimeSpan _throttleInterval = throttleInterval;
    private readonly Func<string, CancellationToken, Task> _editMessageText = editMessageText ?? throw new ArgumentNullException(nameof(editMessageText));
    private readonly Func<VideoProcessingStage, string> _formatStage = formatStage ?? DefaultFormatStage;

    private DateTimeOffset _lastEditAt = DateTimeOffset.MinValue;
    private string _lastText = string.Empty;
    private PendingUpdate? _pending;

    public async Task ReportAsync(VideoProcessingProgress progress, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        if (_pending is not null && now >= _pending.Value.DueAt)
        {
            var pendingText = _pending.Value.Text;
            _pending = null;
            await EditIfChangedAsync(pendingText, now, ct);
            return;
        }

        var pct = Math.Clamp(progress.Percent, 0, 100);
        var text = BuildText(pct, progress.Stage);

        if (text == _lastText)
        {
            return;
        }

        if (now - _lastEditAt >= _throttleInterval)
        {
            await EditIfChangedAsync(text, now, ct);
            return;
        }

        var dueAt = _lastEditAt == DateTimeOffset.MinValue
            ? now
            : _lastEditAt + _throttleInterval;
        _pending = new PendingUpdate(text, dueAt);
    }

    private async Task EditIfChangedAsync(string text, DateTimeOffset now, CancellationToken ct)
    {
        if (text == _lastText)
        {
            return;
        }

        _lastText = text;
        _lastEditAt = now;
        await _editMessageText(text, ct);
    }

    private string BuildText(int pct, VideoProcessingStage stage)
    {
        var filled = pct / 10;
        var bar = new string('#', filled) + new string('-', 10 - filled);
        var stageText = _formatStage(stage);

        return
            "\u2699\uFE0F <b>GPU ОБРОБКА: В ПРОЦЕСІ</b>\n\n" +
            $"<blockquote>\U0001F464 <code>{H(_fileName)}</code></blockquote>\n\n" +
            $"\U0001F4CA <code>[{bar}] {pct}%</code>\n" +
            $"\U0001F504 <i>Stage: {H(stageText)}</i>";
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private static string DefaultFormatStage(VideoProcessingStage stage)
        => stage switch
        {
            VideoProcessingStage.Init => "Init",
            VideoProcessingStage.Slicing => "Slicing",
            VideoProcessingStage.Transform => "Transform",
            VideoProcessingStage.Finalizing => "Finalizing",
            _ => stage.ToString()
        };

    private readonly record struct PendingUpdate(string Text, DateTimeOffset DueAt);
}

