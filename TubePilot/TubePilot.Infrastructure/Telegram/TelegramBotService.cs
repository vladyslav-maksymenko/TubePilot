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
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptions;

    private const string SubscriberFile = "telegram_subscriber.txt";

    private readonly ConcurrentDictionary<int, VideoProcessingState> _userSelections = [];
    private readonly ConcurrentDictionary<int, Task> _activeJobs = [];

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
        IVideoProcessor videoProcessor,
        ILogger<TelegramBotService> logger)
    {
        _videoProcessor = videoProcessor;
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

        var text =
            $"\U0001F680 <b>Знайдено нове медіа!</b>\n\n" +
            $"<blockquote>\U0001F464 <b>Файл:</b> <code>{encodedName}</code>\n" +
            $"\U0001F4BE <b>Вага:</b> {sizeMb:F1} MB</blockquote>\n\n" +
            $"\U0001F3AF Оберіть фільтри унікалізації й натисніть <b>Почати обробку</b> \U0001F447";

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
        if (message.Text != "/start")
        {
            return;
        }

        var chatId = message.Chat.Id;

        if (!IsAuthorized(chatId))
        {
            _logger.LogWarning("Unauthorized /start from ChatId: {ChatId}", chatId);
            await _botClient.SendMessage(chatId, "\U0001F6AB Доступ заборонено.", cancellationToken: ct);
            return;
        }

        await File.WriteAllTextAsync(SubscriberFile, chatId.ToString(), ct);

        var text =
            "\u2705 <b>Авторизація успішна!</b>\n\n" +
            "Тепер я буду надсилати сюди інтерфейс для обробки кожного нового відео, яке потрапляє на Google Drive \U0001F6F8";

        await _botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
        _logger.LogInformation("Successfully linked bot to user ChatId: {ChatId}", chatId);
    }

    private async Task ProcessCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var msgId = query.Message?.MessageId ?? 0;
        var chatId = query.Message?.Chat.Id ?? 0;
        var data = query.Data ?? "";

        if (!IsAuthorized(chatId))
        {
            await _botClient.AnswerCallbackQuery(query.Id, "\U0001F6AB Доступ заборонено.", showAlert: true, cancellationToken: ct);
            return;
        }

        if (!_userSelections.TryGetValue(msgId, out var state))
        {
            await _botClient.AnswerCallbackQuery(
                query.Id,
                "\u23F3 Сесія застаріла. Завантажте нове відео.",
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
                    await _botClient.AnswerCallbackQuery(query.Id, "\u26A0\uFE0F Оберіть бодай один фільтр.", showAlert: true, cancellationToken: ct);
                    return;
                }

                await _botClient.AnswerCallbackQuery(query.Id, "Запуск обробки...", cancellationToken: ct);
                await _botClient.EditMessageText(
                    chatId,
                    msgId,
                    $"\u2699\uFE0F <b>GPU ОБРОБКА: АКТИВНО</b>\n\n<blockquote>\U0001F464 <code>{H(state.FileName)}</code></blockquote>\n\n\U0001F4CA <code>[----------] 0%</code>\n\U0001F504 <i>Ініціалізація FFmpeg Engine...</i>",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);

                var job = RunProcessingJobAsync(chatId, msgId, state, ct);
                _activeJobs[msgId] = job;
                _ = job.ContinueWith(_ => _activeJobs.TryRemove(msgId, out _!), TaskScheduler.Default);
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
#if false
                var now = DateTime.UtcNow;
                var pct = Math.Clamp(progress.Percent, 0, 100);
                var stage = progress.Stage;
                var stageChanged = stage != lastStage;

                if (!stageChanged && pct == lastPct)
                {
                    return;
                }

                if (!stageChanged && (now - lastUpdate).TotalSeconds < 2 && pct < 100)
                {
                    return;
                }

                lastUpdate = now;
                lastStage = stage;
                lastPct = pct;

                var filled = pct / 10;
                var bar = new string('#', filled) + new string('-', 10 - filled);

                var text =
                    $"\u2699\uFE0F <b>GPU ОБРОБКА: В ПРОЦЕСІ</b>\n\n<blockquote>\U0001F464 <code>{H(state.FileName)}</code></blockquote>\n\n\U0001F4CA <code>[{bar}] {pct}%</code>\n\U0001F504 <i>Render Engine (FFmpeg)...</i>";

                text = text.Replace("Render Engine (FFmpeg)...", $"Stage: {FormatStage(lastStage)}");

                if (text == lastText)
                {
                    return;
                }

                lastText = text;

                try
                {
                    await _botClient.EditMessageText(chatId, msgId, text, parseMode: ParseMode.Html, cancellationToken: ct);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 429)
                {
                    _logger.LogDebug(ex, "Telegram rejected progress update (chatId={ChatId}, msgId={MsgId}).", chatId, msgId);
                }
#endif
            }, ct);

            var finalTxt =
                $"\u2705 <b>УНІКАЛІЗАЦІЮ ЗАВЕРШЕНО</b>\n\n" +
                $"<blockquote>\U0001F464 <code>{H(state.FileName)}</code>\n" +
                $"\u26A1 Фільтрів застосовано: {state.SelectedOptions.Count}</blockquote>";

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
                        $"\U0001F3AC <b>ГОТОВИЙ ФАЙЛ:</b>\n<code>{H(fileName)}</code>\n\n" +
                        $"\U0001F4C1 Локально: <code>{H(absoluteLocalPath)}</code>\n" +
                        (string.IsNullOrWhiteSpace(baseUrl)
                            ? ""
                            : $"\U0001F517 URL: <code>{H(url)}</code>\n") +
                        "\n\u26A0\uFE0F Telegram не відкриває локальні/внутрішні URL у кнопках. " +
                        "Щоб мати кнопку — задай публічний https URL у <code>Telegram:BaseUrl</code> (наприклад, через ngrok).";

                    await _botClient.SendMessage(chatId, msgTextNoButton, parseMode: ParseMode.Html, cancellationToken: ct);
                    continue;
                }

                var msgText =
                    $"\U0001F3AC <b>ГОТОВИЙ ФАЙЛ:</b>\n<code>{H(fileName)}</code>\n\n" +
                    $"\u25B6\uFE0F <a href=\"{H(url)}\">ДИВИТИСЬ РЕЗУЛЬТАТ</a>";

                var replyMarkup = new InlineKeyboardMarkup([[InlineKeyboardButton.WithUrl("\U0001F517 ВІДКРИТИ РЕЗУЛЬТАТ", url)]]);

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
                $"\u274C <b>CRITICAL FAILURE</b>\n\n<pre>{H(ex.Message)}</pre>",
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

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private static string FormatStage(VideoProcessingStage stage)
        => stage switch
        {
            VideoProcessingStage.Init => "Init",
            VideoProcessingStage.Slicing => "Slicing",
            VideoProcessingStage.Transform => "Transform",
            VideoProcessingStage.Finalizing => "Finalizing",
            _ => stage.ToString()
        };

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
