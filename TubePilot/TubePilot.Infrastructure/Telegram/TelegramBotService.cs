using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Telegram.Models;
using TubePilot.Infrastructure.Telegram.Options;
using DriveFile = TubePilot.Core.Domain.DriveFile;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramBotService : BackgroundService, ITelegramBotService
{
    private readonly ITelegramBotClient _botClient;
    private readonly IVideoProcessor _videoProcessor;
    private readonly TelegramProcessingQueue _processingQueue;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptions;

    private const string SubscriberFile = "telegram_subscriber.txt";

    private readonly ConcurrentDictionary<(long ChatId, int MessageId), VideoProcessingState> _userSelections = [];
    private readonly ConcurrentDictionary<(long ChatId, int MessageId), Task> _activeJobs = [];

    private static readonly Dictionary<string, string> OptionLabels = new()
    {
        { "mirror", "🪞 Дзеркало (HFlip)" },
        { "reduce_audio", "\U0001F509 Гучність -15%" },
        { "slow_down", "\U0001F40C Delay 4-7%" },
        { "speed_up", "\u26A1 Speed 3-5%" },
        { "color_correct", "\U0001F3A8 Корекція кольору" },
        { "slice", "\u2702\uFE0F Шортс (2:30-3:10)" },
        { "slice_long", "\u2702\uFE0F Long (5:10-7:10)" },
        { "qr_overlay", "\U0001F4F1 Віджет QR" },
        { "rotate", "\U0001F504 Захисний поворот" },
        { "downscale_1080p", "\U0001F4D0 Даунскейл 1080p" }
    };

    public TelegramBotService(
        IOptionsMonitor<TelegramOptions> options,
        IVideoProcessor videoProcessor,
        TelegramProcessingQueue processingQueue,
        ILogger<TelegramBotService> logger)
    {
        _videoProcessor = videoProcessor;
        _processingQueue = processingQueue;
        _logger = logger;
        _telegramOptions = options;

        var token = options.CurrentValue.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogCritical("Telegram Bot Token is missing in secrets.json!");
            throw new ArgumentException("Telegram Bot Token is required to start the service.");
        }

        _botClient = new TelegramBotClient(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, stoppingToken);

        var me = await _botClient.GetMe(stoppingToken);
        _logger.LogInformation("[Telegram] Bot @{Username} is listening for interactions...", me.Username);

        await Task.Delay(-1, stoppingToken);
    }

    public async Task NotifyNewVideoAsync(DriveFile file, string localPath, CancellationToken ct = default)
    {
        long chatId = 0;

        if (File.Exists(SubscriberFile) && long.TryParse(await File.ReadAllTextAsync(SubscriberFile, ct), out var savedId))
        {
            chatId = savedId;
        }

        if (chatId == 0)
        {
            _logger.LogWarning("Ніхто не підписаний на бота. Напиши /start боту в Telegram.");
            return;
        }

        var sizeMb = file.SizeBytes / (1024.0 * 1024.0);
        var encodedName = H(file.Name);

        var startText =
            $"\U0001F680 <b>Знайдено нове медіа!</b>\n\n" +
            $"<blockquote>\U0001F464 <b>Файл:</b> <code>{encodedName}</code>\n" +
            $"\U0001F4BE <b>Вага:</b> {sizeMb:F1} MB</blockquote>\n\n" +
            $"\U0001F3AF Оберіть фільтри унікалізації й натисніть <b>Почати обробку</b> \U0001F447";

        var state = new VideoProcessingState { FileId = file.Id, FileName = file.Name, LocalPath = localPath };

        var msg = await _botClient.SendMessage(
            chatId: chatId,
            text: startText,
            parseMode: ParseMode.Html,
            replyMarkup: BuildKeyboard(state),
            cancellationToken: ct);

        _userSelections[(chatId, msg.MessageId)] = state;
    }

    private InlineKeyboardMarkup BuildKeyboard(VideoProcessingState state)
    {
        var buttons = new List<IEnumerable<InlineKeyboardButton>>();

        foreach (var opt in OptionLabels)
        {
            var isSelected = state.SelectedOptions.Contains(opt.Key);
            var check = isSelected ? "\u2705" : "\U0001F518";
            buttons.Add([InlineKeyboardButton.WithCallbackData($"{check} {opt.Value}", $"t|{opt.Key}")]);
        }

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData("\U0001F4A0 Вибрати всі", "all"),
            InlineKeyboardButton.WithCallbackData("\u2716\uFE0F Очистити", "none")
        ]);

        buttons.Add([InlineKeyboardButton.WithCallbackData("\u25B6\uFE0F ПОЧАТИ ОБРОБКУ", "start")]);

        return new InlineKeyboardMarkup(buttons);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            await (update switch
            {
                { CallbackQuery: { } query } => ProcessCallbackAsync(query, ct),
                { Message: { } message } => ProcessMessageAsync(message, ct),
                _ => Task.CompletedTask
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process UI callback or message.");
        }
    }

    private bool IsAuthorized(long chatId)
    {
        var allowed = _telegramOptions.CurrentValue.AllowedChatId;
        return allowed is null || allowed == chatId;
    }

    private async Task ProcessMessageAsync(Message message, CancellationToken ct)
    {
        var commandText = message.Text?.Trim() ?? string.Empty;
        if (commandText != "/start" && !commandText.StartsWith("/dev_queue", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var chatId = message.Chat.Id;

        if (commandText.StartsWith("/dev_queue", StringComparison.OrdinalIgnoreCase))
        {
            await ProcessDevQueueCommandAsync(chatId, commandText, ct);
            return;
        }

        if (!IsAuthorized(chatId))
        {
            _logger.LogWarning("Unauthorized /start from ChatId: {ChatId}", chatId);
            await _botClient.SendMessage(chatId, "🚫 Доступ заборонено.", cancellationToken: ct);
            return;
        }

        await File.WriteAllTextAsync(SubscriberFile, chatId.ToString(), ct);

        var startText =
            "✅ <b>Авторизація успішна!</b>\n\n" +
            "Тепер я буду надсилати сюди інтерфейс для обробки кожного нового відео, яке потрапляє на Google Drive 🚸";

        await _botClient.SendMessage(chatId, startText, parseMode: ParseMode.Html, cancellationToken: ct);
        _logger.LogInformation("Successfully linked bot to user ChatId: {ChatId}", chatId);
    }

    private async Task ProcessCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var msgId = query.Message?.MessageId ?? 0;
        var chatId = query.Message?.Chat.Id ?? 0;
        var data = query.Data ?? string.Empty;
        var selectionKey = (chatId, msgId);

        if (!IsAuthorized(chatId))
        {
            await _botClient.AnswerCallbackQuery(query.Id, "🚫 Доступ заборонено.", showAlert: true, cancellationToken: ct);
            return;
        }

        if (!_userSelections.TryGetValue(selectionKey, out var state))
        {
            await _botClient.AnswerCallbackQuery(
                query.Id,
                "⏳ Сесія застаріла. Завантажте нове відео.",
                showAlert: true,
                cancellationToken: ct);
            return;
        }

        var updateKeyboard = true;

        switch (data)
        {
            case var d when d.StartsWith("t|", StringComparison.Ordinal):
                var optId = d.Split('|')[1];
                if (!state.SelectedOptions.Add(optId))
                {
                    state.SelectedOptions.Remove(optId);
                }
                break;
            case "all":
                foreach (var k in OptionLabels.Keys)
                {
                    state.SelectedOptions.Add(k);
                }
                break;
            case "none":
                state.SelectedOptions.Clear();
                break;
            case "start":
                updateKeyboard = false;
                if (state.SelectedOptions.Count == 0)
                {
                    await _botClient.AnswerCallbackQuery(query.Id, "⚠️ Оберіть бодай один фільтр.", showAlert: true, cancellationToken: ct);
                    return;
                }

                _userSelections.TryRemove(selectionKey, out _);

                var admission = await _processingQueue.EnqueueAsync(
                    chatId,
                    msgId,
                    onQueuedAsync: async (position, callbackCt) => await EditQueuedMessageAsync(chatId, msgId, state, position, callbackCt),
                    onStartAsync: async callbackCt => await EditProcessingStartMessageAsync(chatId, msgId, state, callbackCt),
                    processAsync: callbackCt => RunProcessingPipelineAsync(chatId, msgId, state, callbackCt),
                    ct);

                if (admission.Status == TelegramProcessingQueue.QueueAdmissionStatus.Duplicate)
                {
                    await _botClient.AnswerCallbackQuery(query.Id, "⏳ Уже в черзі чи в обробці.", cancellationToken: ct);
                    return;
                }

                _activeJobs[selectionKey] = admission.LifecycleTask;
                _ = admission.LifecycleTask.ContinueWith(_ => _activeJobs.TryRemove(selectionKey, out _!), TaskScheduler.Default);

                var queueText = admission.Status == TelegramProcessingQueue.QueueAdmissionStatus.Queued
                    ? $"В черзі: #{admission.Position}"
                    : "Обробка запущена";

                await _botClient.AnswerCallbackQuery(query.Id, queueText, cancellationToken: ct);
                return;
            default:
                updateKeyboard = false;
                break;
        }

        if (updateKeyboard)
        {
            await _botClient.EditMessageReplyMarkup(chatId, msgId, replyMarkup: BuildKeyboard(state), cancellationToken: ct);
            await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        }
    }

    private async Task ProcessDevQueueCommandAsync(long chatId, string text, CancellationToken ct)
    {
        if (!IsAuthorized(chatId))
        {
            await _botClient.SendMessage(chatId, "Access denied.", cancellationToken: ct);
            return;
        }

        var options = _telegramOptions.CurrentValue;
        if (!options.DevCommandsEnabled)
        {
            await _botClient.SendMessage(
                chatId,
                "Dev commands disabled. Enable `Telegram:DevCommandsEnabled=true` to use /dev_queue.",
                cancellationToken: ct);
            return;
        }

        var parsedCount = TryParseTrailingInt(text, defaultValue: 2);
        var count = Math.Clamp(parsedCount, 1, 5);
        var durationSeconds = Math.Clamp(options.DevSimulatedProcessingSeconds, 5, 600);

        await _botClient.SendMessage(
            chatId,
            $"DEV queue test: enqueueing {count} job(s). MaxConcurrentJobs={options.MaxConcurrentJobs}. SimulatedDuration={durationSeconds}s.\n" +
            "Expected when MaxConcurrentJobs=1: job #1 processing, job #2 queued (#1).",
            cancellationToken: ct);

        for (var index = 1; index <= count; index++)
        {
            var fileName = $"DEV_QUEUE_JOB_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{index:00}.mp4";
            var state = new VideoProcessingState { FileId = "dev", FileName = fileName, LocalPath = "dev://simulated" };

            var msg = await _botClient.SendMessage(
                chatId,
                $"DEV job: {fileName}",
                cancellationToken: ct);

            var selectionKey = (chatId, msg.MessageId);

            var admission = await _processingQueue.EnqueueAsync(
                chatId,
                msg.MessageId,
                onQueuedAsync: async (position, callbackCt) => await EditQueuedMessageAsync(chatId, msg.MessageId, state, position, callbackCt),
                onStartAsync: async callbackCt => await EditProcessingStartMessageAsync(chatId, msg.MessageId, state, callbackCt),
                processAsync: callbackCt => RunSimulatedProcessingPipelineAsync(chatId, msg.MessageId, state, TimeSpan.FromSeconds(durationSeconds), callbackCt),
                ct);

            _activeJobs[selectionKey] = admission.LifecycleTask;
            _ = admission.LifecycleTask.ContinueWith(_ => _activeJobs.TryRemove(selectionKey, out _!), TaskScheduler.Default);
        }
    }

    private static int TryParseTrailingInt(string input, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return defaultValue;
        }

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return defaultValue;
        }

        return int.TryParse(parts[^1], out var parsed)
            ? parsed
            : defaultValue;
    }

    private Task EditQueuedMessageAsync(long chatId, int msgId, VideoProcessingState state, int queuePosition, CancellationToken ct)
        => _botClient.EditMessageText(
            chatId,
            msgId,
            TelegramProcessingMessageTemplates.BuildQueuedStatusText(state.FileName, queuePosition),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

    private Task EditProcessingStartMessageAsync(long chatId, int msgId, VideoProcessingState state, CancellationToken ct)
        => _botClient.EditMessageText(
            chatId,
            msgId,
            TelegramProcessingMessageTemplates.BuildProcessingStartText(state.FileName),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

    private async Task RunSimulatedProcessingPipelineAsync(
        long chatId,
        int msgId,
        VideoProcessingState state,
        TimeSpan duration,
        CancellationToken ct)
    {
        var reporter = new TelegramProcessingProgressReporter(
            state.FileName,
            TimeProvider.System,
            throttleInterval: TimeSpan.FromSeconds(1),
            editMessageText: async (text, callbackCt) =>
            {
                try
                {
                    await _botClient.EditMessageText(chatId, msgId, text, parseMode: ParseMode.Html, cancellationToken: callbackCt);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 429)
                {
                    _logger.LogDebug(ex, "Telegram rejected simulated progress update (chatId={ChatId}, msgId={MsgId}).", chatId, msgId);
                }
            });

        var startedAt = TimeProvider.System.GetUtcNow();
        var totalMs = Math.Max(1, duration.TotalMilliseconds);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var elapsedMs = (TimeProvider.System.GetUtcNow() - startedAt).TotalMilliseconds;
            var pct = (int)Math.Clamp(Math.Floor(elapsedMs / totalMs * 100), 0, 99);
            await reporter.ReportAsync(new VideoProcessingProgress(pct, VideoProcessingStage.Transform), ct);

            if (elapsedMs >= totalMs)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        await reporter.ReportAsync(new VideoProcessingProgress(100, VideoProcessingStage.Finalizing), ct);

        await _botClient.EditMessageText(
            chatId,
            msgId,
            $"✅ <b>DEV job completed</b>\n\n<blockquote>\U0001F464 <code>{H(state.FileName)}</code></blockquote>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private async Task RunProcessingPipelineAsync(long chatId, int msgId, VideoProcessingState state, CancellationToken ct)
    {
        try
        {
            var reporter = new TelegramProcessingProgressReporter(
                state.FileName,
                TimeProvider.System,
                throttleInterval: TimeSpan.FromSeconds(2),
                editMessageText: async (text, callbackCt) =>
                {
                    try
                    {
                        await _botClient.EditMessageText(chatId, msgId, text, parseMode: ParseMode.Html, cancellationToken: callbackCt);
                    }
                    catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 429)
                    {
                        _logger.LogDebug(ex, "Telegram rejected progress update (chatId={ChatId}, msgId={MsgId}).", chatId, msgId);
                    }
                });

            var results = await _videoProcessor.ProcessAsync(state.LocalPath, state.SelectedOptions, async progress =>
            {
                await reporter.ReportAsync(progress, ct);
                return;
            }, ct);

            var finalTxt =
                "✅ <b>УНІКАЛІЗАЦІЮ ЗАВЕРШЕНО</b>\n\n" +
                $"<blockquote>👤 <code>{H(state.FileName)}</code>\n" +
                $"⚡ Фільтрів застосовано: {state.SelectedOptions.Count}</blockquote>";

            await _botClient.EditMessageText(chatId, msgId, finalTxt, parseMode: ParseMode.Html, cancellationToken: ct);

            foreach (var res in results)
            {
                var absoluteLocalPath = Path.GetFullPath(res);
                var fileName = Path.GetFileName(absoluteLocalPath) ?? absoluteLocalPath;

                var baseUrl = _telegramOptions.CurrentValue.BaseUrl?.TrimEnd('/') ?? string.Empty;
                var url = $"{baseUrl}/play/{Uri.EscapeDataString(fileName)}";

                if (!IsTelegramSafeButtonUrl(url))
                {
                    var msgTextNoButton =
                        $"🎬 <b>ГОТОВИЙ ФАЙЛ:</b>\n<code>{H(fileName)}</code>\n\n" +
                        $"📁 Локально: <code>{H(absoluteLocalPath)}</code>\n" +
                        (string.IsNullOrWhiteSpace(baseUrl)
                            ? ""
                            : $"🔗 URL: <code>{H(url)}</code>\n") +
                        "\n⚠️ Telegram не відкриває локальні/внутрішні URL у кнопках. " +
                        "Щоб мати кнопку — задай публічний https URL у <code>Telegram:BaseUrl</code> (наприклад, через ngrok).";

                    await _botClient.SendMessage(chatId, msgTextNoButton, parseMode: ParseMode.Html, cancellationToken: ct);
                    continue;
                }

                var msgText =
                    $"🎬 <b>ГОТОВИЙ ФАЙЛ:</b>\n<code>{H(fileName)}</code>\n\n" +
                    $"▶️ <a href=\"{H(url)}\">ДИВИТИСЬ РЕЗУЛЬТАТ</a>";

                var replyMarkup = new InlineKeyboardMarkup([[InlineKeyboardButton.WithUrl("🔗 ВІДКРИТИ РЕЗУЛЬТАТ", url)]]);

                try
                {
                    await _botClient.SendMessage(chatId, msgText, parseMode: ParseMode.Html, replyMarkup: replyMarkup, cancellationToken: ct);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400)
                {
                    await _botClient.SendMessage(chatId, msgText, parseMode: ParseMode.Html, cancellationToken: ct);
                    _logger.LogWarning(ex, "Telegram rejected inline keyboard for URL: {Url}", url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for {FileName}.", state.FileName);
            await _botClient.EditMessageText(
                chatId,
                msgId,
                $"❌ <b>CRITICAL FAILURE</b>\n\n<pre>{H(ex.Message)}</pre>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_activeJobs.IsEmpty)
        {
            _logger.LogInformation("Waiting for {Count} active processing job(s) to complete...", _activeJobs.Count);
            await Task.WhenAll(_activeJobs.Values);
        }

        await base.StopAsync(cancellationToken);
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Telegram polling error");
        return Task.CompletedTask;
    }

    private string BuildQueuedStatusText(VideoProcessingState state, int queuePosition)
        => $"⏳ <b>GPU ОБРОБКА: ЧЕРГА</b>\n\n<blockquote>👤 <code>{H(state.FileName)}</code></blockquote>\n\n📌 <code>Queued (#{queuePosition})</code>\n⏱ <i>Очікує вільний слот...</i>";

    private string BuildProcessingStartText(VideoProcessingState state)
        => $"⚙️ <b>GPU ОБРОБКА: АКТИВНО</b>\n\n<blockquote>👤 <code>{H(state.FileName)}</code></blockquote>\n\n📊 <code>[----------] 0%</code>\n🔄 <i>Ініціалізація FFmpeg Engine...</i>";

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private static bool IsTelegramSafeButtonUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return false;
        }

        if (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))
        {
            return false;
        }

        return true;
    }
}
