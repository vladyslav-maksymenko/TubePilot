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
using TubePilot.Infrastructure.YouTube.Options;
using DriveFile = TubePilot.Core.Domain.DriveFile;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramBotService : BackgroundService, ITelegramBotService
{
    private const string SubscriberFile = "telegram_subscriber.txt";
    private const string PublishResultPrefix = "res:";
    private const string PublishWizardPrefix = "pw:";

    private readonly ITelegramBotClient _botClient;
    private readonly IVideoProcessor _videoProcessor;
    private readonly IYouTubeUploader _youTubeUploader;
    private readonly IGoogleSheetsLogger _googleSheetsLogger;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptions;
    private readonly IOptionsMonitor<PublishingOptions> _publishingOptions;
    private readonly IOptionsMonitor<YouTubeOptions> _youTubeOptions;
    private CancellationToken _serviceStoppingToken;

    private readonly ConcurrentDictionary<int, VideoProcessingState> _userSelections = [];
    private readonly ConcurrentDictionary<int, Task> _activeProcessingJobs = [];
    private readonly ConcurrentDictionary<long, PublishWizardSession> _publishSessionsByChatId = [];
    private readonly ConcurrentDictionary<long, Task> _activePublishJobsByChatId = [];
    private readonly ConcurrentDictionary<int, PublishedResultContext> _publishedResultsByMessageId = [];

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
        IVideoProcessor videoProcessor,
        IYouTubeUploader youTubeUploader,
        IGoogleSheetsLogger googleSheetsLogger,
        ILogger<TelegramBotService> logger)
    {
        _videoProcessor = videoProcessor;
        _youTubeUploader = youTubeUploader;
        _googleSheetsLogger = googleSheetsLogger;
        _logger = logger;
        _telegramOptions = options;
        _publishingOptions = publishingOptions;
        _youTubeOptions = youTubeOptions;

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

        var msg = await _botClient.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: BuildKeyboard(state),
            cancellationToken: ct);

        _userSelections[msg.MessageId] = state;
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
        var chatId = message.Chat.Id;
        var text = message.Text?.Trim();

        if (text is "/start")
        {
            if (!IsAuthorized(chatId))
            {
                _logger.LogWarning("Unauthorized /start from ChatId: {ChatId}", chatId);
                await _botClient.SendMessage(chatId, "🚫 Доступ заборонено.", cancellationToken: ct);
                return;
            }

            await File.WriteAllTextAsync(SubscriberFile, chatId.ToString(), ct);

            var startText =
                "✅ <b>Авторизація успішна!</b>\n\n" +
                "Тепер я буду надсилати сюди інтерфейс для обробки кожного нового відео, яке потрапляє на Google Drive 🚀";

            await _botClient.SendMessage(chatId, startText, parseMode: ParseMode.Html, cancellationToken: ct);
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

        if (!IsAuthorized(chatId))
        {
            await _botClient.AnswerCallbackQuery(query.Id, "🚫 Доступ заборонено.", showAlert: true, cancellationToken: ct);
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

        if (!_userSelections.TryGetValue(msgId, out var state))
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

                await _botClient.AnswerCallbackQuery(query.Id, "Запуск обробки...", cancellationToken: ct);
                await _botClient.EditMessageText(
                    chatId,
                    msgId,
                    $"⚙️ <b>GPU ОБРОБКА: АКТИВНО</b>\n\n<blockquote>👤 <code>{H(state.FileName)}</code></blockquote>\n\n📊 <code>[----------] 0%</code>\n🔄 <i>Ініціалізація FFmpeg Engine...</i>",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);

                var job = RunProcessingJobAsync(chatId, msgId, state, _serviceStoppingToken);
                _activeProcessingJobs[msgId] = job;
                _ = job.ContinueWith(_ => _activeProcessingJobs.TryRemove(msgId, out _), TaskScheduler.Default);
                break;
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

    private async Task HandlePublishedResultCallbackAsync(
        CallbackQuery query,
        long chatId,
        int msgId,
        string action,
        CancellationToken ct)
    {
        switch (action)
        {
            case "publish":
                if (_publishSessionsByChatId.ContainsKey(chatId))
                {
                    await _botClient.AnswerCallbackQuery(
                        query.Id,
                        "Спочатку заверши або скасуй поточний wizard.",
                        showAlert: true,
                        cancellationToken: ct);
                    return;
                }

                if (!_publishedResultsByMessageId.TryGetValue(msgId, out var resultContext))
                {
                    await _botClient.AnswerCallbackQuery(
                        query.Id,
                        "Не вдалося знайти дані для публікації.",
                        showAlert: true,
                        cancellationToken: ct);
                    return;
                }

                var session = new PublishWizardSession(resultContext, chatId);
                _publishSessionsByChatId[chatId] = session;
                await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                await SendTitlePromptAsync(session, ct);
                return;

            case "cancel":
                if (_publishSessionsByChatId.ContainsKey(chatId))
                {
                    await CancelPublishWizardAsync(chatId, "❌ Публікацію скасовано.", ct);
                }
                else
                {
                    await _botClient.AnswerCallbackQuery(query.Id, "Немає активної публікації.", showAlert: false, cancellationToken: ct);
                }

                return;

            default:
                await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                return;
        }
    }

    private async Task HandleWizardCallbackAsync(CallbackQuery query, long chatId, string action, CancellationToken ct)
    {
        if (!_publishSessionsByChatId.TryGetValue(chatId, out var session))
        {
            await _botClient.AnswerCallbackQuery(query.Id, "Wizard вже завершено.", showAlert: true, cancellationToken: ct);
            return;
        }

        switch (action)
        {
            case "use-file-name":
                if (session.Step != PublishWizardStep.WaitingForTitle)
                {
                    await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                    return;
                }

                session.Title = PublishingScheduleHelper.GetDefaultTitle(session.ResultContext.ResultFileName);
                await _botClient.AnswerCallbackQuery(query.Id, "Використано ім'я файлу.", cancellationToken: ct);
                await SendDescriptionPromptAsync(session, ct);
                return;

            case "cancel":
                await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                await CancelPublishWizardAsync(chatId, "❌ Публікацію скасовано.", ct);
                return;

            case "schedule-now":
                if (session.Step != PublishWizardStep.WaitingForScheduleChoice)
                {
                    await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                    return;
                }

                session.ScheduledPublishAtUtc = null;
                await _botClient.AnswerCallbackQuery(query.Id, "Публікація буде одразу.", cancellationToken: ct);
                await SendConfirmPromptAsync(session, ct);
                return;

            case "schedule-pick":
                if (session.Step != PublishWizardStep.WaitingForScheduleChoice)
                {
                    await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                    return;
                }

                await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                await SendCustomDatePromptAsync(session, ct);
                return;

            case "confirm":
                if (session.Step != PublishWizardStep.Confirm)
                {
                    await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                    return;
                }

                await _botClient.AnswerCallbackQuery(query.Id, "Починаю upload...", cancellationToken: ct);
                await StartUploadAsync(session, _serviceStoppingToken);
                return;

            default:
                await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
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
            case PublishWizardStep.WaitingForTitle:
                if (string.IsNullOrWhiteSpace(text))
                {
                    await _botClient.SendMessage(session.ChatId, "Заголовок не може бути порожнім. Введи його ще раз:", cancellationToken: ct);
                    return;
                }

                session.Title = text.Trim();
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
                        DateTimeOffset.UtcNow,
                        out var scheduledPublishAtUtc,
                        out var errorMessage))
                {
                    await _botClient.SendMessage(session.ChatId, errorMessage, cancellationToken: ct);
                    return;
                }

                session.ScheduledPublishAtUtc = scheduledPublishAtUtc;
                await SendConfirmPromptAsync(session, ct);
                return;

            case PublishWizardStep.Confirm:
                await _botClient.SendMessage(session.ChatId, "Підтверди публікацію кнопкою нижче або /cancel.", cancellationToken: ct);
                return;

            case PublishWizardStep.Uploading:
                await _botClient.SendMessage(session.ChatId, "Upload вже триває. Дочекайся завершення.", cancellationToken: ct);
                return;

            default:
                return;
        }
    }
    private async Task SendTitlePromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForTitle;
        var text =
            "📝 <b>Введи заголовок (Title):</b>\n\n" +
            $"Файл: <code>{H(session.ResultContext.ResultFileName)}</code>";

        var message = await _botClient.SendMessage(
            session.ChatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(
            [
                [InlineKeyboardButton.WithCallbackData("✅ Використати ім'я файлу", $"{PublishWizardPrefix}use-file-name")],
                [InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]
            ]),
            cancellationToken: ct);

        session.PromptMessageId = message.MessageId;
    }

    private async Task SendDescriptionPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForDescription;
        await _botClient.SendMessage(
            session.ChatId,
            "🧾 Введи опис (Description) або /skip:",
            cancellationToken: ct);
    }

    private async Task SendTagsPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForTags;
        await _botClient.SendMessage(
            session.ChatId,
            "🏷️ Введи теги через кому або /skip:",
            cancellationToken: ct);
    }

    private async Task SendSchedulePromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForScheduleChoice;

        await _botClient.SendMessage(
            session.ChatId,
            "🗓️ Коли публікувати?",
            replyMarkup: new InlineKeyboardMarkup(
            [
                [InlineKeyboardButton.WithCallbackData("🚀 Зараз", $"{PublishWizardPrefix}schedule-now")],
                [InlineKeyboardButton.WithCallbackData("🕒 Вибрати дату і час", $"{PublishWizardPrefix}schedule-pick")],
                [InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]
            ]),
            cancellationToken: ct);
    }

    private async Task SendCustomDatePromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.WaitingForCustomDate;
        var timeZoneId = _publishingOptions.CurrentValue.TimeZoneId;
        await _botClient.SendMessage(
            session.ChatId,
            $"Введи дату і час у форматі: YYYY-MM-DD HH:mm (наприклад 2026-03-26 21:11)\nTimezone: {timeZoneId}",
            cancellationToken: ct);
    }

    private async Task SendConfirmPromptAsync(PublishWizardSession session, CancellationToken ct)
    {
        session.Step = PublishWizardStep.Confirm;

        var description = string.IsNullOrWhiteSpace(session.Description) ? "/skip" : session.Description;
        var tags = session.Tags.Count == 0 ? "/skip" : string.Join(", ", session.Tags);
        var publishTime = PublishingScheduleHelper.FormatPublishTime(session.ScheduledPublishAtUtc, _publishingOptions.CurrentValue.TimeZoneId);

        var text =
            "📋 <b>Підтвердження upload</b>\n\n" +
            $"<blockquote>\n" +
            $"📁 Файл: <code>{H(session.ResultContext.ResultFileName)}</code>\n" +
            $"📝 Title: <code>{H(session.Title)}</code>\n" +
            $"🧾 Description: <code>{H(description)}</code>\n" +
            $"🏷️ Tags: <code>{H(tags)}</code>\n" +
            $"🗓️ Publish: <code>{H(publishTime)}</code>\n" +
            $"</blockquote>";

        var message = await _botClient.SendMessage(
            session.ChatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(
            [
                [InlineKeyboardButton.WithCallbackData("✅ Підтвердити upload", $"{PublishWizardPrefix}confirm")],
                [InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{PublishWizardPrefix}cancel")]
            ]),
            cancellationToken: ct);

        session.SummaryMessageId = message.MessageId;
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
            await _botClient.EditMessageText(
                session.ChatId,
                session.SummaryMessageId.Value,
                BuildProgressText(session, 0),
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        else
        {
            var message = await _botClient.SendMessage(
                session.ChatId,
                BuildProgressText(session, 0),
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            session.ProgressMessageId = message.MessageId;
        }

        var job = RunUploadJobAsync(session, linkedCts.Token);
        _activePublishJobsByChatId[session.ChatId] = job;
        _ = job.ContinueWith(_ =>
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
            var request = new YouTubeUploadRequest(
                session.ResultContext.ResultFilePath,
                session.Title,
                session.Description,
                session.Tags,
                session.ScheduledPublishAtUtc,
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

            await SendUploadSuccessAsync(session, result, ct);
        }
        catch (OperationCanceledException)
        {
            await SendUploadCancelledAsync(session, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "YouTube upload failed for {FileName}.", session.ResultContext.ResultFileName);
            await SendUploadFailureAsync(session, ex, ct);
        }
        finally
        {
            _publishSessionsByChatId.TryRemove(session.ChatId, out _);
        }
    }

    private async Task UpdateUploadProgressAsync(PublishWizardSession session, int percent, CancellationToken ct)
    {
        if (session.ProgressMessageId is null || percent <= session.LastProgressPercent)
        {
            return;
        }

        session.LastProgressPercent = percent;
        await _botClient.EditMessageText(
            session.ChatId,
            session.ProgressMessageId.Value,
            BuildProgressText(session, percent),
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }

    private static string BuildProgressText(PublishWizardSession session, int percent)
    {
        var label = session.ScheduledPublishAtUtc is null ? "Published" : "Scheduled";
        return
            $"📤 <b>{label} upload to YouTube...</b>\n\n" +
            $"<blockquote>📁 <code>{H(session.ResultContext.ResultFileName)}</code>\n" +
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
            await _botClient.EditMessageText(
                session.ChatId,
                session.ProgressMessageId.Value,
                text,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        else
        {
            await _botClient.SendMessage(session.ChatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    private async Task SendUploadFailureAsync(PublishWizardSession session, Exception ex, CancellationToken ct)
    {
        var text =
            $"❌ <b>Upload failed</b>\n\n" +
            $"<pre>{H(ex.Message)}</pre>";

        if (session.ProgressMessageId is not null)
        {
            await _botClient.EditMessageText(
                session.ChatId,
                session.ProgressMessageId.Value,
                text,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        else
        {
            await _botClient.SendMessage(session.ChatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    private async Task SendUploadCancelledAsync(PublishWizardSession session, CancellationToken ct)
    {
        var text = "❌ <b>Upload cancelled</b>";

        if (session.ProgressMessageId is not null)
        {
            await _botClient.EditMessageText(
                session.ChatId,
                session.ProgressMessageId.Value,
                text,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        else
        {
            await _botClient.SendMessage(session.ChatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    private async Task CancelPublishWizardAsync(long chatId, string message, CancellationToken ct)
    {
        if (_publishSessionsByChatId.TryRemove(chatId, out var session))
        {
            session.UploadCancellation?.Cancel();
        }

        await _botClient.SendMessage(chatId, message, cancellationToken: ct);
    }
    private async Task RunProcessingJobAsync(long chatId, int msgId, VideoProcessingState state, CancellationToken ct)
    {
        try
        {
            var lastUpdate = DateTime.MinValue;
            var lastText = string.Empty;

            var results = await _videoProcessor.ProcessAsync(state.LocalPath, state.SelectedOptions, async pct =>
            {
                if ((DateTime.UtcNow - lastUpdate).TotalSeconds < 2 && pct < 100)
                {
                    return;
                }

                lastUpdate = DateTime.UtcNow;

                var filled = pct / 10;
                var bar = new string('#', filled) + new string('-', 10 - filled);

                var text =
                    $"⚙️ <b>GPU ОБРОБКА: В ПРОЦЕСІ</b>\n\n<blockquote>👤 <code>{H(state.FileName)}</code></blockquote>\n\n📊 <code>[{bar}] {pct}%</code>\n🔄 <i>Render Engine (FFmpeg)...</i>";

                if (text == lastText)
                {
                    return;
                }

                lastText = text;

                try
                {
                    await _botClient.EditMessageText(chatId, msgId, text, parseMode: ParseMode.Html, cancellationToken: ct);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400)
                {
                    _logger.LogDebug(ex, "Telegram rejected progress update (chatId={ChatId}, msgId={MsgId}).", chatId, msgId);
                }
            }, ct);

            var finalTxt =
                $"✅ <b>УНІКАЛІЗАЦІЮ ЗАВЕРШЕНО</b>\n\n" +
                $"<blockquote>👤 <code>{H(state.FileName)}</code>\n" +
                $"⚡ Фільтрів застосовано: {state.SelectedOptions.Count}</blockquote>";

            await _botClient.EditMessageText(chatId, msgId, finalTxt, parseMode: ParseMode.Html, cancellationToken: ct);

            foreach (var res in results)
            {
                var absoluteLocalPath = Path.GetFullPath(res);
                var fileName = Path.GetFileName(absoluteLocalPath) ?? absoluteLocalPath;
                var resultUrl = BuildPublicResultUrl(fileName);

                var context = new PublishedResultContext(
                    state.FileName,
                    fileName,
                    absoluteLocalPath,
                    resultUrl);

                var message = await _botClient.SendMessage(
                    chatId,
                    BuildResultMessage(context),
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildResultKeyboard(context),
                    cancellationToken: ct);

                _publishedResultsByMessageId[message.MessageId] = context;
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

    private string BuildPublicResultUrl(string fileName)
    {
        var baseUrl = _telegramOptions.CurrentValue.BaseUrl?.TrimEnd('/') ?? string.Empty;
        return string.IsNullOrWhiteSpace(baseUrl)
            ? string.Empty
            : $"{baseUrl}/play/{Uri.EscapeDataString(fileName)}";
    }

    private static string BuildResultMessage(PublishedResultContext context)
    {
        var lines = new List<string>
        {
            "🎬 <b>ГОТОВИЙ ФАЙЛ:</b>",
            $"<code>{H(context.ResultFileName)}</code>",
            string.Empty,
            $"📁 Локально: <code>{H(context.ResultFilePath)}</code>"
        };

        if (!string.IsNullOrWhiteSpace(context.PublicUrl))
        {
            lines.Add($"🔗 URL: <code>{H(context.PublicUrl)}</code>");
        }

        return string.Join('\n', lines);
    }

    private static InlineKeyboardMarkup BuildResultKeyboard(PublishedResultContext context)
    {
        var buttons = new List<IEnumerable<InlineKeyboardButton>>();

        if (!string.IsNullOrWhiteSpace(context.PublicUrl) && IsTelegramSafeButtonUrl(context.PublicUrl))
        {
            buttons.Add([InlineKeyboardButton.WithUrl("🔗 Відкрити результат", context.PublicUrl)]);
        }

        buttons.Add(
        [
            InlineKeyboardButton.WithCallbackData("📤 Опублікувати на YouTube", $"{PublishResultPrefix}publish"),
            InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"{PublishResultPrefix}cancel")
        ]);

        return new InlineKeyboardMarkup(buttons);
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
