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
using TubePilot.Infrastructure.Tunnel;
using TubePilot.Infrastructure.YouTube;
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
    private readonly IYouTubeChannelLookup _youTubeChannelLookup;
    private readonly IChannelStore _channelStore;
    private readonly TelegramProcessingQueue _processingQueue;
    private readonly TelegramPublishQueue _publishQueue;
    private readonly TelegramResultCardPublisher _resultCardPublisher;
    private readonly TelegramUploadJobRunner _uploadJobRunner;
    private readonly TelegramChannelManagementHandler _channelHandler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptions;
    private readonly IOptionsMonitor<PublishingOptions> _publishingOptions;
    private readonly IOptionsMonitor<YouTubeOptions> _youTubeOptions;
    private readonly NgrokTunnelManager _tunnel;
    private Task<string?>? _tunnelTask;
    private CancellationToken _serviceStoppingToken;

    private readonly ConcurrentDictionary<(long ChatId, int MessageId), VideoProcessingState> _userSelections = [];
    private readonly ConcurrentDictionary<(long ChatId, int MessageId), Task> _activeProcessingJobs = [];
    private readonly ConcurrentDictionary<long, PublishWizardSession> _publishSessionsByChatId = [];
    private readonly ConcurrentDictionary<(long ChatId, int MessageId), Task> _activePublishJobs = [];
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
        IYouTubeChannelLookup youTubeChannelLookup,
        IChannelStore channelStore,
        TelegramProcessingQueue processingQueue,
        TelegramPublishQueue publishQueue,
        NgrokTunnelManager tunnel,
        TelegramResultCardPublisher resultCardPublisher,
        TelegramUploadJobRunner uploadJobRunner,
        TelegramChannelManagementHandler channelHandler,
        TimeProvider timeProvider,
        ILogger<TelegramBotService> logger)
    {
        _videoProcessor = videoProcessor;
        _youTubeChannelLookup = youTubeChannelLookup;
        _channelStore = channelStore;
        _processingQueue = processingQueue;
        _publishQueue = publishQueue;
        _tunnel = tunnel;
        _resultCardPublisher = resultCardPublisher;
        _uploadJobRunner = uploadJobRunner;
        _channelHandler = channelHandler;
        _timeProvider = timeProvider;
        _logger = logger;
        _telegramOptions = options;
        _publishingOptions = publishingOptions;
        _youTubeOptions = youTubeOptions;
        _botClient = botClient;
        _ui = uiClient;
    }

    private static readonly ReplyKeyboardMarkup PersistentKeyboard = new(
    [
        [new KeyboardButton("📋 Мої групи каналів"), new KeyboardButton("➕ Додати групу")],
        [new KeyboardButton("❓ Допомога")]
    ])
    {
        ResizeKeyboard = true,
        IsPersistent = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceStoppingToken = stoppingToken;
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, stoppingToken);
        _tunnelTask = _tunnel.StartAsync(5000, _telegramOptions.CurrentValue.NgrokAuthToken ?? "", stoppingToken);

        await _botClient.SetMyCommands(
        [
            new BotCommand { Command = "start", Description = "Авторизація бота" },
            new BotCommand { Command = "channels", Description = "Керування групами та каналами" },
            new BotCommand { Command = "help", Description = "Список команд та пояснення" },
            new BotCommand { Command = "cancel", Description = "Скасувати поточну дію" },
        ], cancellationToken: stoppingToken);

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
                "Тепер я буду надсилати сюди інтерфейс для обробки кожного нового відео, яке потрапляє на Google Drive 🚀\n\n" +
                "Використовуй кнопки нижче або /help для списку команд.";

            await _ui.SendMessageWithReplyKeyboardAsync(chatId, startText, PersistentKeyboard, parseMode: ParseMode.Html, ct: ct);
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
            _channelHandler.CancelWizard(chatId);
            if (_publishSessionsByChatId.ContainsKey(chatId))
            {
                await CancelPublishWizardAsync(chatId, "❌ Публікацію скасовано.", ct);
            }

            return;
        }

        if (text.Equals("/channels", StringComparison.OrdinalIgnoreCase) || text == "📋 Мої групи каналів")
        {
            await _channelHandler.ShowMainMenuAsync(chatId, ct);
            return;
        }

        if (text == "➕ Додати групу")
        {
            await _channelHandler.HandleCallbackAsync(chatId, "add-group", ct);
            return;
        }

        if (text == "➕ Додати канал")
        {
            var groupId = _channelHandler.GetLastViewedGroupId(chatId);
            if (groupId is not null)
            {
                await _channelHandler.HandleCallbackAsync(chatId, $"add-ch:{groupId}", ct);
            }
            else
            {
                await _ui.SendMessageAsync(chatId, "⚠️ Спочатку відкрий групу через 📋 Мої групи каналів.", ct: ct);
            }
            return;
        }

        if (text.Equals("/help", StringComparison.OrdinalIgnoreCase) || text == "❓ Допомога")
        {
            await SendHelpAsync(chatId, ct);
            return;
        }

        // Channel management wizard has priority over publish wizard
        if (_channelHandler.HasActiveWizard(chatId))
        {
            if (await _channelHandler.HandleTextAsync(chatId, text, ct))
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

        if (data.StartsWith(TelegramChannelManagementHandler.Prefix, StringComparison.Ordinal))
        {
            await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
            await _channelHandler.HandleCallbackAsync(chatId, data[TelegramChannelManagementHandler.Prefix.Length..], ct);
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
                session.ReplyToMessageId = msgId;
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
                bulkSession.ReplyToMessageId = msgId;
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
    {
        var jobs = _activePublishJobs
            .Where(kvp => kvp.Key.ChatId == chatId)
            .Select(kvp => kvp.Value)
            .ToArray();

        return jobs.Length == 0 ? Task.CompletedTask : Task.WhenAll(jobs);
    }

    private async Task HandleWizardCallbackAsync(CallbackQuery query, long chatId, string action, CancellationToken ct)
    {
        if (!_publishSessionsByChatId.TryGetValue(chatId, out var session))
        {
            await _ui.AnswerCallbackQueryAsync(query.Id, "Wizard вже завершено.", showAlert: true, ct: ct);
            return;
        }

        if (action.StartsWith("channel-store:", StringComparison.Ordinal))
        {
            if (session.Step != PublishWizardStep.WaitingForChannel)
            {
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                return;
            }

            var parts = action["channel-store:".Length..].Split(':');
            if (parts.Length != 2)
            {
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                return;
            }

            var groupId = parts[0];
            var channelId = parts[1];
            var group = _channelStore.GetGroup(groupId);
            var channel = group?.Channels.FirstOrDefault(c => c.Id == channelId);

            if (group is null || channel is null)
            {
                await _ui.AnswerCallbackQueryAsync(query.Id, "❌ Канал не знайдено.", showAlert: true, ct: ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(channel.RefreshToken))
            {
                await _ui.AnswerCallbackQueryAsync(query.Id, "⚠️ Для цього каналу не налаштовано OAuth токен. Додай через /channels.", showAlert: true, ct: ct);
                return;
            }

            // Check quota
            _channelStore.ResetQuotaIfNeeded(groupId);
            var remaining = _channelStore.GetRemainingQuota(groupId);
            if (remaining < 1650)
            {
                await _ui.AnswerCallbackQueryAsync(query.Id, "🔴 Квота вичерпана для цієї групи. Спробуй завтра.", showAlert: true, ct: ct);
                return;
            }

            session.ChannelName = channel.Name;
            session.StoreChannelId = channelId;
            session.StoreGroupId = groupId;
            await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
            await SendTitlePromptAsync(session, ct);
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
            var channels = session.AvailableChannels;
            if (channels.Count == 0)
            {
                channels = ResolveConfiguredChannels();
            }
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

        if (action.StartsWith("visibility:", StringComparison.Ordinal))
        {
            if (session.Step != PublishWizardStep.WaitingForVisibility)
            {
                await _ui.AnswerCallbackQueryAsync(query.Id, ct: ct);
                return;
            }

            var value = action["visibility:".Length..];
            session.Visibility = value switch
            {
                "public" => YouTubeVideoVisibility.Public,
                "unlisted" => YouTubeVideoVisibility.Unlisted,
                "private" => YouTubeVideoVisibility.Private,
                "skip" => YouTubeVideoVisibility.Public,
                _ => YouTubeVideoVisibility.Public
            };

            var label = session.Visibility switch
            {
                YouTubeVideoVisibility.Public => "Public",
                YouTubeVideoVisibility.Unlisted => "Unlisted",
                YouTubeVideoVisibility.Private => "Private",
                _ => "Public"
            };

            await _ui.AnswerCallbackQueryAsync(query.Id, $"Visibility: {label}", ct: ct);
            await SendConfirmPromptAsync(session, ct);
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
                session.Visibility = YouTubeVideoVisibility.Public;
                await _ui.AnswerCallbackQueryAsync(query.Id, "Публікація буде одразу.", ct: ct);
                await SendVisibilityPromptAsync(session, ct);
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
                session.Visibility = YouTubeVideoVisibility.Public;
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

                await EnqueueUploadAsync(query, session, _serviceStoppingToken);
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
                await _ui.SendMessageAsync(session.ChatId, "Обери канал кнопкою нижче або /cancel.", replyToMessageId: session.ReplyToMessageId, ct: ct);
                return;

            case PublishWizardStep.WaitingForTitle:
                if (string.IsNullOrWhiteSpace(text))
                {
                    await _ui.SendMessageAsync(session.ChatId, "Заголовок не може бути порожнім. Введи його ще раз:", replyToMessageId: session.ReplyToMessageId, ct: ct);
                    return;
                }

                if (session.IsBulkPublish)
                {
                    var template = text.Trim();
                    if (!template.Contains("{N}", StringComparison.Ordinal))
                    {
                        await _ui.SendMessageAsync(session.ChatId, "Шаблон має містити {N} (номер частини). Спробуй ще раз:", replyToMessageId: session.ReplyToMessageId, ct: ct);
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
                    await _ui.SendMessageAsync(session.ChatId, errorMessage, replyToMessageId: session.ReplyToMessageId, ct: ct);
                    return;
                }

                session.ScheduledPublishAtUtc = scheduledPublishAtUtc;
                session.Visibility = YouTubeVideoVisibility.Public;
                await SendConfirmPromptAsync(session, ct);
                return;

            case PublishWizardStep.WaitingForVisibility:
                await _ui.SendMessageAsync(session.ChatId, "Обери visibility кнопкою нижче або /cancel.", replyToMessageId: session.ReplyToMessageId, ct: ct);
                return;

            case PublishWizardStep.Confirm:
                await _ui.SendMessageAsync(session.ChatId, "Підтверди публікацію кнопкою нижче або /cancel.", replyToMessageId: session.ReplyToMessageId, ct: ct);
                return;

            case PublishWizardStep.Uploading:
                await _ui.SendMessageAsync(session.ChatId, "Upload вже триває. Дочекайся завершення.", replyToMessageId: session.ReplyToMessageId, ct: ct);
                return;

            default:
                return;
        }
    }

    private async Task SendChannelPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForChannel;

        // Build channel list from channel store (primary) + fallback to old config/API
        var rows = new List<InlineKeyboardButton[]>();
        var channelEntries = new List<(string Label, string CallbackData)>();

        var groups = _channelStore.GetAllGroups();
        foreach (var group in groups)
        {
            foreach (var ch in group.Channels)
            {
                var hasToken = !string.IsNullOrWhiteSpace(ch.RefreshToken);
                var icon = hasToken ? "📺" : "⚠️";
                channelEntries.Add(($"{icon} {ch.Name} ({group.Name})", $"{PublishWizardPrefix}channel-store:{group.Id}:{ch.Id}"));
            }
        }

        if (channelEntries.Count == 0)
        {
            // Fallback to old behavior
            var legacyChannels = await ResolveChannelChoicesAsync(ct);
            session.AvailableChannels = legacyChannels;
            for (var index = 0; index < legacyChannels.Count; index++)
            {
                rows.Add([InlineKeyboardButton.WithCallbackData($"📺 {legacyChannels[index]}", $"{PublishWizardPrefix}channel:{index}")]);
            }
        }
        else
        {
            foreach (var entry in channelEntries)
            {
                rows.Add([InlineKeyboardButton.WithCallbackData(entry.Label, entry.CallbackData)]);
            }
        }

        rows.Add([InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]);

        await _ui.SendMessageAsync(
            session.ChatId,
            "📺 Обери канал для публікації:",
            replyToMessageId: session.ReplyToMessageId,
            replyMarkup: new InlineKeyboardMarkup(rows),
            ct: ct);
    }

    private async Task<IReadOnlyList<string>> ResolveChannelChoicesAsync(CancellationToken ct)
    {
        try
        {
            var channels = await _youTubeChannelLookup.GetChannelsAsync(ct);
            var titles = channels
                .Select(c => c.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (titles.Length > 0)
            {
                return titles;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve YouTube channels from API; falling back to configured list.");
        }

        var configured = ResolveConfiguredChannels();
        return configured.Count == 0 ? ["Default"] : configured;
    }

    private IReadOnlyList<string> ResolveConfiguredChannels()
        => (_publishingOptions.CurrentValue.YouTubeChannels ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
            replyToMessageId: session.ReplyToMessageId,
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
        await _ui.SendMessageAsync(session.ChatId, "🧾 Введи опис (Description) або /skip:", replyToMessageId: session.ReplyToMessageId, ct: ct);
    }

    private async Task SendTagsPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForTags;
        await _ui.SendMessageAsync(session.ChatId, "🏷️ Введи теги через кому або /skip:", replyToMessageId: session.ReplyToMessageId, ct: ct);
    }

    private async Task SendSchedulePromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForScheduleChoice;

        await _ui.SendMessageAsync(
            session.ChatId,
            "🗓️ Коли публікувати?",
            replyToMessageId: session.ReplyToMessageId,
            replyMarkup: new InlineKeyboardMarkup(
            [
                [InlineKeyboardButton.WithCallbackData("🚀 Зараз", $"{PublishWizardPrefix}schedule-now")],
                [InlineKeyboardButton.WithCallbackData("⏭️ Next free slot", $"{PublishWizardPrefix}schedule-next")],
                [InlineKeyboardButton.WithCallbackData("🕒 Вибрати дату і час", $"{PublishWizardPrefix}schedule-pick")],
                [InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]
            ]),
            ct: ct);
    }

    private async Task SendVisibilityPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForVisibility;

        await _ui.SendMessageAsync(
            session.ChatId,
            "🌐 Обери visibility відео на YouTube:",
            replyToMessageId: session.ReplyToMessageId,
            replyMarkup: new InlineKeyboardMarkup(
            [
                [
                    InlineKeyboardButton.WithCallbackData("🌍 Public (default)", $"{PublishWizardPrefix}visibility:public"),
                    InlineKeyboardButton.WithCallbackData("🔗 Unlisted", $"{PublishWizardPrefix}visibility:unlisted")
                ],
                [InlineKeyboardButton.WithCallbackData("🔒 Private", $"{PublishWizardPrefix}visibility:private")],
                [InlineKeyboardButton.WithCallbackData("⏩ Skip (Public)", $"{PublishWizardPrefix}visibility:skip")],
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
            replyToMessageId: session.ReplyToMessageId,
            ct: ct);
    }

    private async Task SendConfirmPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.Confirm;

        var channelName = string.IsNullOrWhiteSpace(session.ChannelName) ? "Default" : session.ChannelName;
        var description = string.IsNullOrWhiteSpace(session.Description) ? "/skip" : session.Description;
        var tags = session.Tags.Count == 0 ? "/skip" : string.Join(", ", session.Tags);
        var publishTime = PublishingScheduleHelper.FormatPublishTime(session.ScheduledPublishAtUtc, _publishingOptions.CurrentValue.TimeZoneId);
        var visibility = session.ScheduledPublishAtUtc is null
            ? session.Visibility switch
            {
                YouTubeVideoVisibility.Public => "public",
                YouTubeVideoVisibility.Unlisted => "unlisted",
                YouTubeVideoVisibility.Private => "private",
                _ => "public"
            }
            : "public (scheduled)";

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
            $"🌐 Visibility: <code>{H(visibility)}</code>\n" +
            $"🗓️ Publish: <code>{H(publishTime)}</code>\n" +
            $"</blockquote>";

        var summaryId = await _ui.SendMessageAsync(
            session.ChatId,
            text,
            parseMode: ParseMode.Html,
            replyToMessageId: session.ReplyToMessageId,
            replyMarkup: new InlineKeyboardMarkup(
            [
                [InlineKeyboardButton.WithCallbackData("✅ Підтвердити upload", $"{PublishWizardPrefix}confirm")],
                [InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]
            ]),
            ct: ct);

        session.SummaryMessageId = summaryId;
    }

    private async Task EnqueueUploadAsync(CallbackQuery query, PublishWizardSession session, CancellationToken ct)
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
        }
        else
        {
            var messageId = await _ui.SendMessageAsync(
                session.ChatId,
                "⏳ Готую upload...",
                parseMode: ParseMode.Html,
                replyToMessageId: session.ReplyToMessageId,
                ct: ct);
            session.ProgressMessageId = messageId;
        }

        if (session.ProgressMessageId is null)
        {
            linkedCts.Dispose();
            session.UploadCancellation?.Dispose();
            session.UploadCancellation = null;
            return;
        }

        // Remove the confirm keyboard to avoid accidental double-clicks.
        try
        {
            await _botClient.EditMessageReplyMarkup(session.ChatId, session.ProgressMessageId.Value, replyMarkup: null, cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 429)
        {
            _logger.LogDebug(ex, "Failed to remove Telegram markup for publish message (chatId={ChatId}, msgId={MsgId}).", session.ChatId, session.ProgressMessageId);
        }

        // Release the wizard immediately: user can start another publish while this upload is queued/running.
        _publishSessionsByChatId.TryRemove(session.ChatId, out _);

        var admission = await _publishQueue.EnqueueAsync(
            session.ChatId,
            session.ProgressMessageId.Value,
            onQueuedAsync: async (position, callbackCt) => await EditPublishQueuedMessageAsync(session, position, callbackCt),
            onStartAsync: async callbackCt => await EditPublishStartMessageAsync(session, callbackCt),
            processAsync: callbackCt => RunUploadJobAsync(session, callbackCt),
            linkedCts.Token);

        if (admission.Status == TelegramPublishQueue.QueueAdmissionStatus.Duplicate)
        {
            await _ui.AnswerCallbackQueryAsync(query.Id, "⏳ Уже в черзі чи в публікації.", ct: ct);
            linkedCts.Dispose();
            session.UploadCancellation?.Dispose();
            session.UploadCancellation = null;
            return;
        }

        var jobKey = (session.ChatId, session.ProgressMessageId.Value);
        _activePublishJobs[jobKey] = admission.LifecycleTask;
        _ = admission.LifecycleTask.ContinueWith(_ =>
        {
            linkedCts.Dispose();
            session.UploadCancellation?.Dispose();
            session.UploadCancellation = null;
            _activePublishJobs.TryRemove(jobKey, out _);
        }, TaskScheduler.Default);

        var ackText = admission.Status == TelegramPublishQueue.QueueAdmissionStatus.Queued
            ? $"В черзі: #{admission.Position}"
            : "Upload стартував";

        await _ui.AnswerCallbackQueryAsync(query.Id, ackText, ct: ct);
    }

    private Task EditPublishQueuedMessageAsync(PublishWizardSession session, int queuePosition, CancellationToken ct)
        => session.ProgressMessageId is null
            ? Task.CompletedTask
            : _ui.EditMessageTextAsync(
                session.ChatId,
                session.ProgressMessageId.Value,
                BuildPublishQueuedText(session, queuePosition),
                parseMode: ParseMode.Html,
                ct: ct);

    private Task EditPublishStartMessageAsync(PublishWizardSession session, CancellationToken ct)
    {
        if (session.ProgressMessageId is null)
        {
            return Task.CompletedTask;
        }

        // The uploader itself reports initial progress (0%) right away.
        // Mark it as reported to avoid duplicate edits that Telegram rejects with "message is not modified".
        session.LastProgressPercent = 0;
        return _ui.EditMessageTextAsync(
            session.ChatId,
            session.ProgressMessageId.Value,
            TelegramUploadJobRunner.BuildProgressText(session, 0),
            parseMode: ParseMode.Html,
            ct: ct);
    }

    private static string BuildPublishQueuedText(PublishWizardSession session, int position)
    {
        var context = session.ResultContext;

        var titleLine = session.IsBulkPublish
            ? $"📝 <code>{H(session.TitleTemplate)}</code>"
            : $"📝 <code>{H(session.Title)}</code>";

        return
            $"⏳ <b>Upload queued</b> (#{position})\n\n" +
            $"<blockquote>📁 <code>{H(context.SourceFileName)}</code>\n" +
            $"{titleLine}</blockquote>\n\n" +
            "Очікуй старту…";
    }

    private async Task RunUploadJobAsync(PublishWizardSession session, CancellationToken ct)
    {
        try
        {
            await _uploadJobRunner.RunAsync(
                session,
                RecordLastScheduledAt,
                ResolveNextSlotForBulk,
                ct);
        }
        finally
        {
            _publishSessionsByChatId.TryRemove(session.ChatId, out _);
        }
    }

    private DateTimeOffset? ResolveNextSlotForBulk(PublishWizardSession session)
    {
        var scheduleKey = (session.ChatId, NormalizeChannelName(session.ChannelName));
        _lastScheduledAtUtcByChatAndChannel.TryGetValue(scheduleKey, out var lastUtc);
        var opts = _publishingOptions.CurrentValue;
        return PublishingScheduleHelper.GetNextFreeSlotUtc(
            _timeProvider.GetUtcNow(),
            lastUtc == default ? null : lastUtc,
            opts.TimeZoneId,
            opts.DailyPublishTime);
    }

    private void RecordLastScheduledAt(PublishWizardSession session, YouTubeUploadResult result)
    {
        var key = (session.ChatId, NormalizeChannelName(session.ChannelName));
        _lastScheduledAtUtcByChatAndChannel[key] = result.ScheduledAtUtc ?? _timeProvider.GetUtcNow();
    }

    private async Task CancelPublishWizardAsync(long chatId, string message, CancellationToken ct)
    {
        int? replyToMessageId = null;
        if (_publishSessionsByChatId.TryRemove(chatId, out var session))
        {
            session.UploadCancellation?.Cancel();
            replyToMessageId = session.ReplyToMessageId;
        }

        await _ui.SendMessageAsync(chatId, message, replyToMessageId: replyToMessageId, ct: ct);
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
        var baseUrl = (_tunnel.PublicUrl ?? _telegramOptions.CurrentValue.BaseUrl)?.TrimEnd('/') ?? string.Empty;
        return TelegramResultLinks.BuildPublicResultUrl(baseUrl, fileName);
    }

    private async Task SendHelpAsync(long chatId, CancellationToken ct)
    {
        const string helpText =
            "📖 <b>Команди та можливості</b>\n\n" +

            "<b>🔧 Керування каналами</b>\n" +
            "/channels — список груп і каналів\n" +
            "📋 <i>Мої групи каналів</i> — те саме через кнопку\n" +
            "➕ <i>Додати групу</i> — створити нову групу (Gmail + GCP credentials)\n\n" +

            "<b>📺 Як підключити канал</b>\n" +
            "1. Створи групу → введи Client ID та Client Secret з GCP\n" +
            "2. Додай канал у групу\n" +
            "3. Натисни 🔑 <i>Підключити OAuth</i> на каналі\n" +
            "4. Скопіюй URL → відкрий в Dolphin → пройди OAuth\n" +
            "5. Скопіюй код від Google → відправ боту\n\n" +

            "<b>🎬 Обробка відео</b>\n" +
            "Бот автоматично знаходить нові відео на Google Drive.\n" +
            "Обери фільтри (дзеркало, швидкість, колір тощо) → натисни <i>Почати обробку</i>.\n\n" +

            "<b>📤 Публікація на YouTube</b>\n" +
            "Після обробки натисни <i>Publish to YouTube</i> → обери канал → " +
            "введи назву, опис, теги → обери час → підтверди.\n\n" +

            "<b>⚙️ Інші команди</b>\n" +
            "/start — авторизація бота\n" +
            "/cancel — скасувати поточну дію\n" +
            "/help — ця довідка";

        await _ui.SendMessageAsync(chatId, helpText, parseMode: ParseMode.Html, ct: ct);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_activeProcessingJobs.IsEmpty)
        {
            _logger.LogInformation("Waiting for {Count} active processing job(s) to complete...", _activeProcessingJobs.Count);
            await Task.WhenAll(_activeProcessingJobs.Values);
        }

        if (!_activePublishJobs.IsEmpty)
        {
            _logger.LogInformation("Waiting for {Count} active publish job(s) to complete...", _activePublishJobs.Count);
            await Task.WhenAll(_activePublishJobs.Values);
        }

        await _tunnel.DisposeAsync();
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
