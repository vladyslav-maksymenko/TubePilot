using System.Collections.Concurrent;
using System.Net;
using System.Globalization;
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
using TubePilot.Infrastructure.YouTube.Options;
using DriveFile = TubePilot.Core.Domain.DriveFile;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramBotService : BackgroundService, ITelegramBotService
{
    private const string SubscriberFile = "telegram_subscriber.txt";
    internal const string PublishResultPrefix = "res:";
    private const string PublishWizardPrefix = "pw:";

    private readonly ITelegramBotClient _botClient;
    private readonly ITelegramUiClient _ui;
    private readonly IVideoProcessor _videoProcessor;
    private readonly IYouTubeUploader _youTubeUploader;
    private readonly IGoogleSheetsLogger _googleSheetsLogger;
    private readonly TelegramProcessingQueue _processingQueue;
    private readonly TelegramResultCardPublisher _resultCardPublisher;
    private readonly ITelegramResultThumbnailGenerator _thumbnailGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptions;
    private readonly IOptionsMonitor<PublishingOptions> _publishingOptions;
    private readonly IOptionsMonitor<YouTubeOptions> _youTubeOptions;
    private CancellationToken _serviceStoppingToken;

    private readonly ConcurrentDictionary<(long ChatId, int MessageId), VideoProcessingState> _userSelections = [];
    private readonly ConcurrentDictionary<(long ChatId, int MessageId), Task> _activeProcessingJobs = [];
    private readonly ConcurrentDictionary<long, PublishWizardSession> _publishSessionsByChatId = [];
    private readonly ConcurrentDictionary<long, Task> _activePublishJobsByChatId = [];
    private readonly ConcurrentDictionary<(long ChatId, int MessageId), PublishedResultContext> _publishedResultsByMessageId = [];
    private readonly ConcurrentDictionary<(long ChatId, int GroupId), IReadOnlyList<PublishedResultContext>> _publishedResultGroupsById = [];
    private readonly ConcurrentDictionary<(long ChatId, string ChannelName), DateTimeOffset> _lastScheduledAtUtcByChatAndChannel = [];
    private int _publishedResultGroupCounter;

    private static readonly Dictionary<string, string> OptionLabels = new()
    {
        { "mirror", "\U0001FA9E Дзеркало (HFlip)" },
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
        IOptionsMonitor<PublishingOptions> publishingOptions,
        IOptionsMonitor<YouTubeOptions> youTubeOptions,
        ITelegramBotClient botClient,
        ITelegramUiClient uiClient,
        IVideoProcessor videoProcessor,
        IYouTubeUploader youTubeUploader,
        IGoogleSheetsLogger googleSheetsLogger,
        TelegramProcessingQueue processingQueue,
        TelegramResultCardPublisher resultCardPublisher,
        ITelegramResultThumbnailGenerator thumbnailGenerator,
        TimeProvider timeProvider,
        ILogger<TelegramBotService> logger)
    {
        _videoProcessor = videoProcessor;
        _youTubeUploader = youTubeUploader;
        _googleSheetsLogger = googleSheetsLogger;
        _processingQueue = processingQueue;
        _resultCardPublisher = resultCardPublisher;
        _thumbnailGenerator = thumbnailGenerator;
        _timeProvider = timeProvider;
        _logger = logger;
        _telegramOptions = options;
        _publishingOptions = publishingOptions;
        _youTubeOptions = youTubeOptions;
        _botClient = botClient;
        _ui = uiClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceStoppingToken = stoppingToken;
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

        var text =
            $"🚀 <b>Знайдено нове медіа!</b>\n\n" +
            $"<blockquote>👤 <b>Файл:</b> <code>{encodedName}</code>\n" +
            $"💾 <b>Вага:</b> {sizeMb:F1} MB</blockquote>\n\n" +
            $"🎯 Оберіть фільтри унікалізації й натисніть <b>Почати обробку</b> 👇";

        var state = new VideoProcessingState { FileId = file.Id, FileName = file.Name, LocalPath = localPath };

        var msgId = await _ui.SendMessageAsync(
            chatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: BuildKeyboard(state),
            ct: ct);

        _userSelections[(chatId, msgId)] = state;
    }

    private InlineKeyboardMarkup BuildKeyboard(VideoProcessingState state)
    {
        var buttons = new List<IEnumerable<InlineKeyboardButton>>();

        foreach (var opt in OptionLabels)
        {
            var isSelected = state.SelectedOptions.Contains(opt.Key);
            var check = isSelected ? "✅" : "🔘";
            buttons.Add([InlineKeyboardButton.WithCallbackData($"{check} {opt.Value}", $"t|{opt.Key}")]);
        }

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData("💠 Вибрати всі", "all"),
            InlineKeyboardButton.WithCallbackData("✖️ Очистити", "none")
        ]);

        buttons.Add([InlineKeyboardButton.WithCallbackData("▶️ ПОЧАТИ ОБРОБКУ", "start")]);

        return new InlineKeyboardMarkup(buttons);
    }

    internal async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
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
        var chatId = message.Chat.Id;
        var text = message.Text?.Trim();

        if (text is "/start")
        {
            if (!IsAuthorized(chatId))
            {
                _logger.LogWarning("Unauthorized /start from ChatId: {ChatId}", chatId);
                await _ui.SendMessageAsync(chatId, "🚫 Доступ заборонено.", ct: ct);
                return;
            }

            await File.WriteAllTextAsync(SubscriberFile, chatId.ToString(), ct);

            var startText =
                "✅ <b>Авторизація успішна!</b>\n\n" +
                "Тепер я буду надсилати сюди інтерфейс для обробки кожного нового відео, яке потрапляє на Google Drive 🚀";

            await _ui.SendMessageAsync(chatId, startText, parseMode: ParseMode.Html, ct: ct);
            _logger.LogInformation("Successfully linked bot to user ChatId: {ChatId}", chatId);
            return;
        }

        if (!IsAuthorized(chatId))
        {
            return;
        }

        if (text is null)
        {
            return;
        }

        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            if (_publishSessionsByChatId.ContainsKey(chatId))
            {
                await CancelPublishWizardAsync(chatId, "❌ Публікацію скасовано.", ct);
            }

            return;
        }

        if (_publishSessionsByChatId.TryGetValue(chatId, out var session))
        {
            await HandleWizardTextAsync(session, text, ct);
        }
    }

    private async Task ProcessCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var msgId = query.Message?.MessageId ?? 0;
        var chatId = query.Message?.Chat.Id ?? 0;
        var data = query.Data ?? string.Empty;
        var selectionKey = (chatId, msgId);

        if (!IsAuthorized(chatId))
        {
            await _ui.AnswerCallbackQueryAsync(query.Id, "🚫 Доступ заборонено.", showAlert: true, ct: ct);
            return;
        }

        if (data.StartsWith(PublishResultPrefix, StringComparison.Ordinal))
        {
            await HandlePublishedResultCallbackAsync(query, chatId, msgId, data[PublishResultPrefix.Length..], ct);
            return;
        }

        if (data.StartsWith(PublishWizardPrefix, StringComparison.Ordinal))
        {
            await HandleWizardCallbackAsync(query, chatId, data[PublishWizardPrefix.Length..], ct);
            return;
        }

        if (!_userSelections.TryGetValue(selectionKey, out var state))
        {
            await _ui.AnswerCallbackQueryAsync(
                query.Id,
                "⏳ Сесія застаріла. Завантажте нове відео.",
                showAlert: true,
                ct: ct);
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
                    await _ui.AnswerCallbackQueryAsync(query.Id, "⚠️ Оберіть бодай один фільтр.", showAlert: true, ct: ct);
                    return;
                }

                _userSelections.TryRemove(selectionKey, out _);

                var admission = await _processingQueue.EnqueueAsync(
                    chatId,
                    msgId,
                    onQueuedAsync: async (position, callbackCt) => await EditQueuedMessageAsync(chatId, msgId, state, position, callbackCt),
                    onStartAsync: async callbackCt => await EditProcessingStartMessageAsync(chatId, msgId, state, callbackCt),
                    processAsync: callbackCt => RunProcessingJobAsync(chatId, msgId, state, callbackCt),
                    _serviceStoppingToken);

                if (admission.Status == TelegramProcessingQueue.QueueAdmissionStatus.Duplicate)
                {
                    await _ui.AnswerCallbackQueryAsync(query.Id, "⏳ Уже в черзі чи в обробці.", ct: ct);
                    return;
                }

                _activeProcessingJobs[selectionKey] = admission.LifecycleTask;
                _ = admission.LifecycleTask.ContinueWith(t => _activeProcessingJobs.TryRemove(selectionKey, out _), TaskScheduler.Default);

                var queueText = admission.Status == TelegramProcessingQueue.QueueAdmissionStatus.Queued
                    ? $"В черзі: #{admission.Position}"
                    : "Обробка запущена";

                await _ui.AnswerCallbackQueryAsync(query.Id, queueText, ct: ct);
                return;
            default:
                updateKeyboard = false;
                break;
        }

        if (updateKeyboard)
        {
            await _botClient.EditMessageReplyMarkup(chatId, msgId, replyMarkup: BuildKeyboard(state), cancellationToken: ct);
            await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
        }
    }

    private Task EditQueuedMessageAsync(long chatId, int msgId, VideoProcessingState state, int queuePosition, CancellationToken ct)
        => _ui.EditMessageTextAsync(
            chatId,
            msgId,
            TelegramProcessingMessageTemplates.BuildQueuedStatusText(state.FileName, queuePosition),
            parseMode: ParseMode.Html,
            ct: ct);

    private Task EditProcessingStartMessageAsync(long chatId, int msgId, VideoProcessingState state, CancellationToken ct)
        => _ui.EditMessageTextAsync(
            chatId,
            msgId,
            TelegramProcessingMessageTemplates.BuildProcessingStartText(state.FileName),
            parseMode: ParseMode.Html,
            ct: ct);

    private async Task HandlePublishedResultCallbackAsync(
        CallbackQuery query,
        long chatId,
        int msgId,
        string action,
        CancellationToken ct)
    {
        var parts = action.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.Length > 0 ? parts[0] : string.Empty;

        switch (command)
        {
            case "publish":
                if (_publishSessionsByChatId.ContainsKey(chatId))
                {
                    await _ui.AnswerCallbackQueryAsync(
                        query.Id,
                        "Спочатку заверши або скасуй поточний wizard.",
                        showAlert: true,
                        ct: ct);
                    return;
                }

                PublishedResultContext? resultContext = null;
                if (parts.Length >= 3 &&
                    int.TryParse(parts[1], out var groupId) &&
                    int.TryParse(parts[2], out var groupIndex))
                {
                    resultContext = TryGetGroupResultContext(chatId, groupId, groupIndex);
                }

                if (resultContext is null)
                {
                    _publishedResultsByMessageId.TryGetValue((chatId, msgId), out resultContext);
                }

                if (resultContext is null)
                {
                    await _ui.AnswerCallbackQueryAsync(
                        query.Id,
                        "Не вдалося знайти дані для публікації.",
                        showAlert: true,
                        ct: ct);
                    return;
                }

                var session = new PublishWizardSession([resultContext], chatId);
                _publishSessionsByChatId[chatId] = session;
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                await SendChannelPromptAsync(session, ct);
                return;

            case "publish-all":
                if (_publishSessionsByChatId.ContainsKey(chatId))
                {
                    await _ui.AnswerCallbackQueryAsync(
                        query.Id,
                        "Спочатку заверши або скасуй поточний wizard.",
                        showAlert: true,
                        ct: ct);
                    return;
                }

                if (parts.Length < 2 || !int.TryParse(parts[1], out var bulkGroupId))
                {
                    await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                    return;
                }

                if (!_publishedResultGroupsById.TryGetValue((chatId, bulkGroupId), out var groupContexts) || groupContexts.Count <= 1)
                {
                    await _ui.AnswerCallbackQueryAsync(
                        query.Id,
                        "Не вдалося знайти сегменти для публікації.",
                        showAlert: true,
                        ct: ct);
                    return;
                }

                var orderedContexts = groupContexts
                    .OrderBy(c => c.PartNumber)
                    .ToArray();

                var bulkSession = new PublishWizardSession(orderedContexts, chatId);
                _publishSessionsByChatId[chatId] = bulkSession;
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                await SendChannelPromptAsync(bulkSession, ct);
                return;

            case "cancel":
                if (_publishSessionsByChatId.ContainsKey(chatId))
                {
                    await CancelPublishWizardAsync(chatId, "❌ Публікацію скасовано.", ct);
                }
                else
                {
                    await _ui.AnswerCallbackQueryAsync(query.Id, "Немає активної публікації.", showAlert: false, ct: ct);
                }

                return;

            default:
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                return;
        }
    }

    private PublishedResultContext? TryGetGroupResultContext(long chatId, int groupId, int index)
    {
        if (!_publishedResultGroupsById.TryGetValue((chatId, groupId), out var contexts))
        {
            return null;
        }

        if (index < 0 || index >= contexts.Count)
        {
            return null;
        }

        return contexts[index];
    }

    internal void DebugRegisterPublishedResultGroup(long chatId, int groupId, IReadOnlyList<PublishedResultContext> contexts)
    {
        _publishedResultGroupsById[(chatId, groupId)] = contexts;
    }

    internal Task DebugWaitForActivePublishJobAsync(long chatId)
        => _activePublishJobsByChatId.TryGetValue(chatId, out var job)
            ? job
            : Task.CompletedTask;

    private async Task HandleWizardCallbackAsync(CallbackQuery query, long chatId, string action, CancellationToken ct)
    {
        if (!_publishSessionsByChatId.TryGetValue(chatId, out var session))
        {
            await _ui.AnswerCallbackQueryAsync(query.Id, "Wizard вже завершено.", showAlert: true, ct: ct);
            return;
        }

        if (action.StartsWith("channel:", StringComparison.Ordinal))
        {
            if (session.Step != PublishWizardStep.WaitingForChannel)
            {
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                return;
            }

            var indexText = action["channel:".Length..];
            var channels = _publishingOptions.CurrentValue.YouTubeChannels;
            if (!int.TryParse(indexText, out var channelIndex) ||
                channelIndex < 0 ||
                channelIndex >= channels.Count)
            {
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                return;
            }

            session.ChannelName = channels[channelIndex];
            await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
            await SendTitlePromptAsync(session, ct);
            return;
        }

        switch (action)
        {
            case "use-file-name":
                if (session.Step != PublishWizardStep.WaitingForTitle)
                {
                    await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                    return;
                }

                if (session.IsBulkPublish)
                {
                    var baseTitle = PublishingScheduleHelper.GetDefaultTitle(session.ResultContext.ResultFileName);
                    session.TitleTemplate = $"{baseTitle} Part {{N}}";
                }
                else
                {
                    session.Title = PublishingScheduleHelper.GetDefaultTitle(session.ResultContext.ResultFileName);
                }

                await _ui.AnswerCallbackQueryAsync(query.Id, "Використано ім'я файлу.", ct: ct);
                await SendDescriptionPromptAsync(session, ct);
                return;

            case "cancel":
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                await CancelPublishWizardAsync(chatId, "❌ Публікацію скасовано.", ct);
                return;

            case "schedule-now":
                if (session.Step != PublishWizardStep.WaitingForScheduleChoice)
                {
                    await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                    return;
                }

                session.ScheduledPublishAtUtc = null;
                await _ui.AnswerCallbackQueryAsync(query.Id, "Публікація буде одразу.", ct: ct);
                await SendConfirmPromptAsync(session, ct);
                return;

            case "schedule-next":
                if (session.Step != PublishWizardStep.WaitingForScheduleChoice)
                {
                    await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                    return;
                }

                var scheduleKey = (chatId, NormalizeChannelName(session.ChannelName));
                _lastScheduledAtUtcByChatAndChannel.TryGetValue(scheduleKey, out var lastUtc);
                var nowUtc = _timeProvider.GetUtcNow();
                var nextSlotUtc = PublishingScheduleHelper.GetNextFreeSlotUtc(
                    nowUtc,
                    lastUtc == default ? null : lastUtc,
                    _publishingOptions.CurrentValue.TimeZoneId,
                    _publishingOptions.CurrentValue.DailyPublishTime);

                session.ScheduledPublishAtUtc = nextSlotUtc;
                await _ui.AnswerCallbackQueryAsync(query.Id, "Вибрано наступний слот.", ct: ct);
                await SendConfirmPromptAsync(session, ct);
                return;

            case "schedule-pick":
                if (session.Step != PublishWizardStep.WaitingForScheduleChoice)
                {
                    await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                    return;
                }

                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                await SendCustomDatePromptAsync(session, ct);
                return;

            case "confirm":
                if (session.Step != PublishWizardStep.Confirm)
                {
                    await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                    return;
                }

                await _ui.AnswerCallbackQueryAsync(query.Id, "Починаю upload...", ct: ct);
                await StartUploadAsync(session, _serviceStoppingToken);
                return;

            default:
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                return;
        }
    }

    private async Task HandleWizardTextAsync(PublishWizardSession session, string text, CancellationToken ct)
    {
        if (text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            await CancelPublishWizardAsync(session.ChatId, "❌ Публікацію скасовано.", ct);
            return;
        }

        switch (session.Step)
        {
            case PublishWizardStep.WaitingForChannel:
                await _ui.SendMessageAsync(session.ChatId, "Обери канал кнопкою нижче або /cancel.", ct: ct);
                return;

            case PublishWizardStep.WaitingForTitle:
                if (string.IsNullOrWhiteSpace(text))
                {
                    await _ui.SendMessageAsync(session.ChatId, "Заголовок не може бути порожнім. Введи його ще раз:", ct: ct);
                    return;
                }

                if (session.IsBulkPublish)
                {
                    var template = text.Trim();
                    if (!template.Contains("{N}", StringComparison.Ordinal))
                    {
                        await _ui.SendMessageAsync(session.ChatId, "Шаблон має містити {N} (номер частини). Спробуй ще раз:", ct: ct);
                        return;
                    }

                    session.TitleTemplate = template;
                }
                else
                {
                    session.Title = text.Trim();
                }

                await SendDescriptionPromptAsync(session, ct);
                return;

            case PublishWizardStep.WaitingForDescription:
                session.Description = text.Equals("/skip", StringComparison.OrdinalIgnoreCase) ? string.Empty : text.Trim();
                await SendTagsPromptAsync(session, ct);
                return;

            case PublishWizardStep.WaitingForTags:
                session.Tags = text.Equals("/skip", StringComparison.OrdinalIgnoreCase)
                    ? []
                    : PublishingScheduleHelper.ParseTags(text);
                await SendSchedulePromptAsync(session, ct);
                return;

            case PublishWizardStep.WaitingForCustomDate:
                if (!PublishingScheduleHelper.TryParseScheduledPublishAt(
                        text,
                        _publishingOptions.CurrentValue.TimeZoneId,
                        _timeProvider.GetUtcNow(),
                        out var scheduledPublishAtUtc,
                        out var errorMessage))
                {
                    await _ui.SendMessageAsync(session.ChatId, errorMessage, ct: ct);
                    return;
                }

                session.ScheduledPublishAtUtc = scheduledPublishAtUtc;
                await SendConfirmPromptAsync(session, ct);
                return;

            case PublishWizardStep.Confirm:
                await _ui.SendMessageAsync(session.ChatId, "Підтверди публікацію кнопкою нижче або /cancel.", ct: ct);
                return;

            case PublishWizardStep.Uploading:
                await _ui.SendMessageAsync(session.ChatId, "Upload вже триває. Дочекайся завершення.", ct: ct);
                return;

            default:
                return;
        }
    }

    private async Task SendChannelPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForChannel;

        var channels = _publishingOptions.CurrentValue.YouTubeChannels;
        if (channels.Count == 0)
        {
            channels = ["Default"];
        }

        var rows = new List<InlineKeyboardButton[]>(channels.Count + 1);
        for (var index = 0; index < channels.Count; index++)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData($"📺 {channels[index]}", $"{PublishWizardPrefix}channel:{index}")]);
        }

        rows.Add([InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]);

        await _ui.SendMessageAsync(
            session.ChatId,
            "📺 Обери канал для публікації:",
            replyMarkup: new InlineKeyboardMarkup(rows),
            ct: ct);
    }

    private async Task SendTitlePromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForTitle;

        var text = session.IsBulkPublish
            ? "📝 <b>Введи title template для всіх сегментів:</b>\n\n" +
              "Використай <code>{N}</code> як номер частини (Part 1, Part 2, ...)\n" +
              $"Файл: <code>{H(session.ResultContext.SourceFileName)}</code>\n" +
              $"Сегментів: <b>{session.ResultContexts.Count}</b>"
            : "📝 <b>Введи заголовок (Title):</b>\n\n" +
              $"Файл: <code>{H(session.ResultContext.ResultFileName)}</code>";

        var useFileNameText = session.IsBulkPublish
            ? "✅ Використати ім'я файлу + {N}"
            : "✅ Використати ім'я файлу";

        var promptId = await _ui.SendMessageAsync(
            session.ChatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(
            [
                [InlineKeyboardButton.WithCallbackData(useFileNameText, $"{PublishWizardPrefix}use-file-name")],
                [InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]
            ]),
            ct: ct);

        session.PromptMessageId = promptId;
    }

    private async Task SendDescriptionPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForDescription;
        await _ui.SendMessageAsync(session.ChatId, "🧾 Введи опис (Description) або /skip:", ct: ct);
    }

    private async Task SendTagsPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForTags;
        await _ui.SendMessageAsync(session.ChatId, "🏷️ Введи теги через кому або /skip:", ct: ct);
    }

    private async Task SendSchedulePromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForScheduleChoice;

        await _ui.SendMessageAsync(
            session.ChatId,
            "🗓️ Коли публікувати?",
            replyMarkup: new InlineKeyboardMarkup(
            [
                [InlineKeyboardButton.WithCallbackData("🚀 Зараз", $"{PublishWizardPrefix}schedule-now")],
                [InlineKeyboardButton.WithCallbackData("⏭️ Next free slot", $"{PublishWizardPrefix}schedule-next")],
                [InlineKeyboardButton.WithCallbackData("🕒 Вибрати дату і час", $"{PublishWizardPrefix}schedule-pick")],
                [InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]
            ]),
            ct: ct);
    }

    private async Task SendCustomDatePromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForCustomDate;
        var timeZoneId = _publishingOptions.CurrentValue.TimeZoneId;
        await _ui.SendMessageAsync(
            session.ChatId,
            $"Введи дату і час у форматі: YYYY-MM-DD HH:mm (наприклад 2026-03-26 21:11)\nTimezone: {timeZoneId}",
            ct: ct);
    }

    private async Task SendConfirmPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.Confirm;

        var channelName = string.IsNullOrWhiteSpace(session.ChannelName) ? "Default" : session.ChannelName;
        var description = string.IsNullOrWhiteSpace(session.Description) ? "/skip" : session.Description;
        var tags = session.Tags.Count == 0 ? "/skip" : string.Join(", ", session.Tags);
        var publishTime = PublishingScheduleHelper.FormatPublishTime(session.ScheduledPublishAtUtc, _publishingOptions.CurrentValue.TimeZoneId);

        var titleLine = session.IsBulkPublish
            ? $"📝 Title template: <code>{H(session.TitleTemplate)}</code>\n"
            : $"📝 Title: <code>{H(session.Title)}</code>\n";

        var fileLine = session.IsBulkPublish
            ? $"📁 Source: <code>{H(session.ResultContext.SourceFileName)}</code>\n📦 Segments: <code>{session.ResultContexts.Count}</code>\n"
            : $"📁 Файл: <code>{H(session.ResultContext.ResultFileName)}</code>\n";

        var text =
            "📋 <b>Підтвердження upload</b>\n\n" +
            $"<blockquote>\n" +
            $"📺 Channel: <code>{H(channelName)}</code>\n" +
            fileLine +
            titleLine +
            $"🧾 Description: <code>{H(description)}</code>\n" +
            $"🏷️ Tags: <code>{H(tags)}</code>\n" +
            $"🗓️ Publish: <code>{H(publishTime)}</code>\n" +
            $"</blockquote>";

        var summaryId = await _ui.SendMessageAsync(
            session.ChatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(
            [
                [InlineKeyboardButton.WithCallbackData("✅ Підтвердити upload", $"{PublishWizardPrefix}confirm")],
                [InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]
            ]),
            ct: ct);

        session.SummaryMessageId = summaryId;
    }

    private async Task StartUploadAsync(PublishWizardSession session, CancellationToken ct)
    {
        if (!_publishSessionsByChatId.TryGetValue(session.ChatId, out var currentSession) || !ReferenceEquals(currentSession, session))
        {
            return;
        }

        session.Step = PublishWizardStep.Uploading;
        session.LastProgressPercent = -1;
        session.UploadCancellation?.Dispose();
        session.UploadCancellation = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.UploadCancellation.Token);

        if (session.SummaryMessageId is not null)
        {
            session.ProgressMessageId = session.SummaryMessageId;
            await _ui.EditMessageTextAsync(
                session.ChatId,
                session.SummaryMessageId.Value,
                BuildProgressText(session, 0),
                parseMode: ParseMode.Html,
                ct: ct);
        }
        else
        {
            var messageId = await _ui.SendMessageAsync(
                session.ChatId,
                BuildProgressText(session, 0),
                parseMode: ParseMode.Html,
                ct: ct);
            session.ProgressMessageId = messageId;
        }

        var job = RunUploadJobAsync(session, linkedCts.Token);
        _activePublishJobsByChatId[session.ChatId] = job;
        _ = job.ContinueWith(t =>
        {
            linkedCts.Dispose();
            session.UploadCancellation?.Dispose();
            session.UploadCancellation = null;
            _activePublishJobsByChatId.TryRemove(session.ChatId, out _);
        }, TaskScheduler.Default);
    }

    private async Task RunUploadJobAsync(PublishWizardSession session, CancellationToken ct)
    {
        try
        {
            if (session.IsBulkPublish)
            {
                await RunBulkUploadJobAsync(session, ct);
            }
            else
            {
                await RunSingleUploadJobAsync(session, ct);
            }
        }
        catch (OperationCanceledException)
        {
            await SendUploadCancelledAsync(session, ct);
        }
        catch (Exception ex)
        {
            var context = session.IsBulkPublish
                ? session.ResultContexts[Math.Clamp(session.CurrentBulkIndex, 0, session.ResultContexts.Count - 1)]
                : session.ResultContext;

            _logger.LogError(ex, "YouTube upload failed for {FileName}.", context.ResultFileName);
            await SendUploadFailureAsync(session, ex, ct);
        }
        finally
        {
            _publishSessionsByChatId.TryRemove(session.ChatId, out _);
        }
    }

    private async Task RunSingleUploadJobAsync(PublishWizardSession session, CancellationToken ct)
    {
        string? thumbPath = null;
        try
        {
            thumbPath = await _thumbnailGenerator.TryGenerateAsync(session.ResultContext.ResultFilePath, ct);

            var request = new YouTubeUploadRequest(
                session.ResultContext.ResultFilePath,
                session.Title,
                session.Description,
                session.Tags,
                session.ScheduledPublishAtUtc,
                ThumbnailFilePath: thumbPath,
                CategoryId: _youTubeOptions.CurrentValue.DefaultCategoryId);

            var result = await _youTubeUploader.UploadAsync(
                request,
                percent => UpdateUploadProgressAsync(session, percent, ct),
                ct);

            await _googleSheetsLogger.LogUploadAsync(
                session.ResultContext.SourceFileName,
                session.Title,
                result.VideoId,
                result.YouTubeUrl,
                result.Status.ToString().ToLowerInvariant(),
                result.ScheduledAtUtc,
                ct);

            RecordLastScheduledAt(session, result);
            await SendUploadSuccessAsync(session, result, ct);
        }
        finally
        {
            TryDeleteQuietly(thumbPath);
        }
    }

    private async Task RunBulkUploadJobAsync(PublishWizardSession session, CancellationToken ct)
    {
        var tzId = _publishingOptions.CurrentValue.TimeZoneId;
        var dailyPublishTime = _publishingOptions.CurrentValue.DailyPublishTime;
        var baseScheduledAtUtc = session.ScheduledPublishAtUtc;

        var results = new List<(PublishedResultContext Context, string Title, YouTubeUploadResult Result)>(session.ResultContexts.Count);

        for (var index = 0; index < session.ResultContexts.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            session.CurrentBulkIndex = index;
            session.LastProgressPercent = -1;

            var context = session.ResultContexts[index];
            var title = session.TitleTemplate.Replace("{N}", context.PartNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            DateTimeOffset? scheduledAtUtc;
            if (baseScheduledAtUtc is null)
            {
                if (index == 0)
                {
                    scheduledAtUtc = null;
                }
                else
                {
                    var scheduleKey = (session.ChatId, NormalizeChannelName(session.ChannelName));
                    _lastScheduledAtUtcByChatAndChannel.TryGetValue(scheduleKey, out var lastUtc);
                    scheduledAtUtc = PublishingScheduleHelper.GetNextFreeSlotUtc(
                        _timeProvider.GetUtcNow(),
                        lastUtc == default ? null : lastUtc,
                        tzId,
                        dailyPublishTime);
                }
            }
            else
            {
                scheduledAtUtc = PublishingScheduleHelper.AddLocalDays(baseScheduledAtUtc.Value, index, tzId);
            }

            session.Title = title;
            session.ScheduledPublishAtUtc = scheduledAtUtc;

            string? thumbPath = null;
            try
            {
                thumbPath = await _thumbnailGenerator.TryGenerateAsync(context.ResultFilePath, ct);

                var request = new YouTubeUploadRequest(
                    context.ResultFilePath,
                    title,
                    session.Description,
                    session.Tags,
                    scheduledAtUtc,
                    ThumbnailFilePath: thumbPath,
                    CategoryId: _youTubeOptions.CurrentValue.DefaultCategoryId);

                var result = await _youTubeUploader.UploadAsync(
                    request,
                    percent => UpdateUploadProgressAsync(session, percent, ct),
                    ct);

                await _googleSheetsLogger.LogUploadAsync(
                    context.SourceFileName,
                    title,
                    result.VideoId,
                    result.YouTubeUrl,
                    result.Status.ToString().ToLowerInvariant(),
                    result.ScheduledAtUtc,
                    ct);

                RecordLastScheduledAt(session, result);
                results.Add((context, title, result));
            }
            finally
            {
                TryDeleteQuietly(thumbPath);
            }
        }

        await SendBulkUploadSuccessAsync(session, results, ct);
    }

    private void RecordLastScheduledAt(PublishWizardSession session, YouTubeUploadResult result)
    {
        var key = (session.ChatId, NormalizeChannelName(session.ChannelName));
        _lastScheduledAtUtcByChatAndChannel[key] = result.ScheduledAtUtc ?? _timeProvider.GetUtcNow();
    }

    private static void TryDeleteQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private async Task UpdateUploadProgressAsync(PublishWizardSession session, int percent, CancellationToken ct)
    {
        if (session.ProgressMessageId is null || percent <= session.LastProgressPercent)
        {
            return;
        }

        session.LastProgressPercent = percent;
        await _ui.EditMessageTextAsync(
            session.ChatId,
            session.ProgressMessageId.Value,
            BuildProgressText(session, percent),
            parseMode: ParseMode.Html,
            ct: ct);
    }

    private static string BuildProgressText(PublishWizardSession session, int percent)
    {
        var label = session.ScheduledPublishAtUtc is null ? "Published" : "Scheduled";
        var context = session.IsBulkPublish
            ? session.ResultContexts[Math.Clamp(session.CurrentBulkIndex, 0, session.ResultContexts.Count - 1)]
            : session.ResultContext;

        var bulkLine = session.IsBulkPublish
            ? $"📦 Part {context.PartNumber}/{session.ResultContexts.Count}\n\n"
            : string.Empty;

        return
            $"📤 <b>{label} upload to YouTube...</b>\n\n" +
            bulkLine +
            $"<blockquote>📁 <code>{H(context.ResultFileName)}</code>\n" +
            $"📝 <code>{H(session.Title)}</code></blockquote>\n\n" +
            $"📊 <code>[{new string('#', percent / 10)}{new string('-', 10 - (percent / 10))}] {percent}%</code>";
    }

    private async Task SendUploadSuccessAsync(PublishWizardSession session, YouTubeUploadResult result, CancellationToken ct)
    {
        var text =
            $"✅ <b>{(result.Status == YouTubeUploadStatus.Scheduled ? "Scheduled" : "Published")}</b>\n\n" +
            $"<blockquote>📁 <code>{H(session.ResultContext.ResultFileName)}</code>\n" +
            $"📝 <code>{H(session.Title)}</code>\n" +
            $"🔗 <a href=\"{H(result.YouTubeUrl)}\">Open on YouTube</a></blockquote>";

        if (session.ProgressMessageId is not null)
        {
            await _ui.EditMessageTextAsync(
                session.ChatId,
                session.ProgressMessageId.Value,
                text,
                parseMode: ParseMode.Html,
                ct: ct);
        }
        else
        {
            await _ui.SendMessageAsync(session.ChatId, text, parseMode: ParseMode.Html, ct: ct);
        }
    }

    private async Task SendBulkUploadSuccessAsync(
        PublishWizardSession session,
        IReadOnlyList<(PublishedResultContext Context, string Title, YouTubeUploadResult Result)> results,
        CancellationToken ct)
    {
        var channelName = string.IsNullOrWhiteSpace(session.ChannelName) ? "Default" : session.ChannelName;
        var visible = results.Take(10).ToArray();

        var lines = visible.Select(r =>
            $"• Part {r.Context.PartNumber}: <a href=\"{H(r.Result.YouTubeUrl)}\">{H(r.Title)}</a> ({H(r.Result.Status.ToString())})");

        var suffix = results.Count > visible.Length ? $"\n… +{results.Count - visible.Length} more" : string.Empty;

        var text =
            "✅ <b>Bulk upload completed</b>\n\n" +
            $"<blockquote>📺 <code>{H(channelName)}</code>\n" +
            $"📁 <code>{H(session.ResultContext.SourceFileName)}</code>\n" +
            $"📦 Segments: <b>{results.Count}</b></blockquote>\n\n" +
            string.Join('\n', lines) +
            suffix;

        if (session.ProgressMessageId is not null)
        {
            await _ui.EditMessageTextAsync(
                session.ChatId,
                session.ProgressMessageId.Value,
                text,
                parseMode: ParseMode.Html,
                ct: ct);
        }
        else
        {
            await _ui.SendMessageAsync(session.ChatId, text, parseMode: ParseMode.Html, ct: ct);
        }
    }

    private async Task SendUploadFailureAsync(PublishWizardSession session, Exception ex, CancellationToken ct)
    {
        var text =
            $"❌ <b>Upload failed</b>\n\n" +
            $"<pre>{H(ex.Message)}</pre>";

        if (session.ProgressMessageId is not null)
        {
            await _ui.EditMessageTextAsync(
                session.ChatId,
                session.ProgressMessageId.Value,
                text,
                parseMode: ParseMode.Html,
                ct: ct);
        }
        else
        {
            await _ui.SendMessageAsync(session.ChatId, text, parseMode: ParseMode.Html, ct: ct);
        }
    }

    private async Task SendUploadCancelledAsync(PublishWizardSession session, CancellationToken ct)
    {
        var text = "❌ <b>Upload cancelled</b>";

        if (session.ProgressMessageId is not null)
        {
            await _ui.EditMessageTextAsync(
                session.ChatId,
                session.ProgressMessageId.Value,
                text,
                parseMode: ParseMode.Html,
                ct: ct);
        }
        else
        {
            await _ui.SendMessageAsync(session.ChatId, text, parseMode: ParseMode.Html, ct: ct);
        }
    }

    private async Task CancelPublishWizardAsync(long chatId, string message, CancellationToken ct)
    {
        if (_publishSessionsByChatId.TryRemove(chatId, out var session))
        {
            session.UploadCancellation?.Cancel();
        }

        await _ui.SendMessageAsync(chatId, message, ct: ct);
    }
    private async Task RunProcessingJobAsync(long chatId, int msgId, VideoProcessingState state, CancellationToken ct)
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
                        await _ui.EditMessageTextAsync(chatId, msgId, text, parseMode: ParseMode.Html, ct: callbackCt);
                    }
                    catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 429)
                    {
                        _logger.LogDebug(ex, "Telegram rejected progress update (chatId={ChatId}, msgId={MsgId}).", chatId, msgId);
                    }
                });

            var results = await _videoProcessor.ProcessAsync(state.LocalPath, state.SelectedOptions, async progress =>
            {
                await reporter.ReportAsync(progress, ct);
            }, ct);

            var finalTxt =
                $"✅ <b>УНІКАЛІЗАЦІЮ ЗАВЕРШЕНО</b>\n\n" +
                $"<blockquote>👤 <code>{H(state.FileName)}</code>\n" +
                $"⚡ Фільтрів застосовано: {state.SelectedOptions.Count}</blockquote>";

            await _ui.EditMessageTextAsync(chatId, msgId, finalTxt, parseMode: ParseMode.Html, ct: ct);

            var contexts = new List<PublishedResultContext>(results.Count);
            for (var index = 0; index < results.Count; index++)
            {
                var res = results[index];
                var absoluteLocalPath = Path.GetFullPath(res.OutputPath);
                var fileName = Path.GetFileName(absoluteLocalPath) ?? absoluteLocalPath;
                var resultUrl = BuildPublicResultUrl(fileName);

                var context = new PublishedResultContext(
                    state.FileName,
                    fileName,
                    absoluteLocalPath,
                    resultUrl,
                    res.PartNumber,
                    res.TotalParts,
                    res.DurationSeconds,
                    res.SizeBytes,
                    res.Summary);

                contexts.Add(context);
            }

            var resultGroupId = Interlocked.Increment(ref _publishedResultGroupCounter);
            _publishedResultGroupsById[(chatId, resultGroupId)] = contexts;

            var messages = await _resultCardPublisher.SendResultCardsAsync(chatId, resultGroupId, contexts, ct);
            for (var index = 0; index < messages.Count; index++)
            {
                _publishedResultsByMessageId[(chatId, messages[index].MessageId)] = contexts[index];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for {FileName}.", state.FileName);
            await _ui.EditMessageTextAsync(
                chatId,
                msgId,
                $"❌ <b>CRITICAL FAILURE</b>\n\n<pre>{H(ex.Message)}</pre>",
                parseMode: ParseMode.Html,
                ct: ct);
        }
    }

    private string BuildPublicResultUrl(string fileName)
    {
        var baseUrl = _telegramOptions.CurrentValue.BaseUrl?.TrimEnd('/') ?? string.Empty;
        return TelegramResultLinks.BuildPublicResultUrl(baseUrl, fileName);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_activeProcessingJobs.IsEmpty)
        {
            _logger.LogInformation("Waiting for {Count} active processing job(s) to complete...", _activeProcessingJobs.Count);
            await Task.WhenAll(_activeProcessingJobs.Values);
        }

        if (!_activePublishJobsByChatId.IsEmpty)
        {
            _logger.LogInformation("Waiting for {Count} active publish job(s) to complete...", _activePublishJobsByChatId.Count);
            await Task.WhenAll(_activePublishJobsByChatId.Values);
        }

        await base.StopAsync(cancellationToken);
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Telegram polling error");
        return Task.CompletedTask;
    }

    private static string NormalizeChannelName(string? channelName)
        => string.IsNullOrWhiteSpace(channelName) ? "Default" : channelName.Trim();

    private static string H(string text) => WebUtility.HtmlEncode(text);


}
