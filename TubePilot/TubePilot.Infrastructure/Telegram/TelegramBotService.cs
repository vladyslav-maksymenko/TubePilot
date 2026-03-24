using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
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
        { "mirror", "🪞 Дзеркало (HFlip)" },
        { "reduce_audio", "🔉 Гучність -15%" },
        { "slow_down", "🐌 Delay 4-7%" },
        { "speed_up", "⚡ Speed 3-5%" },
        { "color_correct", "🎨 Корекція кольору" },
        { "slice", "✂️ Шортс (2:30-3:10)" },
        { "slice_long", "✂️ Long (5:10-7:10)" },
        { "qr_overlay", "📱 Віджет QR" },
        { "rotate", "🔄 Захисний поворот" },
        { "downscale_1080p", "📐 Даунскейл 1080p" }
    };

    public TelegramBotService(IOptionsMonitor<TelegramOptions> options, IVideoProcessor videoProcessor, ILogger<TelegramBotService> logger)
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
        var receiverOptions = new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] };

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
            _logger.LogWarning("Ніхто не підписаний на бота! Зайдіть у Telegram і напишіть /start вашому боту.");
            return;
        }

        var sizeMb = file.SizeBytes / (1024.0 * 1024.0);
        
        var text = $"🚀 <b>Знайдено нове медіа!</b>\n\n" +
                   $"<blockquote>👤 <b>Файл:</b> <code>{file.Name}</code>\n" +
                   $"💾 <b>Вага:</b> {sizeMb:F1} MB</blockquote>\n\n" +
                   $"🎯 Оберіть фільтри унікалізації й тисніть <b>Почати обробку</b> 👇";

        var state = new VideoProcessingState { FileId = file.Id, FileName = file.Name, LocalPath = localPath };

        var msg = await _botClient.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: ParseMode.Html,
            replyMarkup: BuildKeyboard(state),
            cancellationToken: ct
        );

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

        buttons.Add([
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
        if (message.Text == "/start")
        {
            var chatId = message.Chat.Id;

            if (!IsAuthorized(chatId))
            {
                _logger.LogWarning("Unauthorized /start from ChatId: {ChatId}", chatId);
                await _botClient.SendMessage(chatId, "⛔ Доступ заборонено.", cancellationToken: ct);
                return;
            }

            await File.WriteAllTextAsync(SubscriberFile, chatId.ToString(), ct);
            
            var text = "✅ <b>Авторизація успішна!</b>\n\nТепер я буду надсилати сюди інтерфейс для обробки кожного нового відео, яке потрапляє на Google Drive 🛸";
            await _botClient.SendMessage(chatId, text, ParseMode.Html, cancellationToken: ct);
            
            _logger.LogInformation("Successfully linked bot to user ChatId: {ChatId}", chatId);
        }
    }

    private async Task ProcessCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var msgId = query.Message?.MessageId ?? 0;
        var chatId = query.Message?.Chat.Id ?? 0;
        var data = query.Data ?? "";

        if (!IsAuthorized(chatId))
        {
            await _botClient.AnswerCallbackQuery(query.Id, "⛔ Доступ заборонено.", showAlert: true, cancellationToken: ct);
            return;
        }

        if (!_userSelections.TryGetValue(msgId, out var state))
        {
            await _botClient.AnswerCallbackQuery(query.Id, "⏳ Сесія застаріла! Завантажте нове відео.", showAlert: true, cancellationToken: ct);
            return;
        }

        var updateKeyboard = true;

        switch (data)
        {
            case var d when d.StartsWith("t|"):
                var optId = d.Split('|')[1];
                if (!state.SelectedOptions.Add(optId))
                {
                    state.SelectedOptions.Remove(optId);
                }
                break;
            case "all":
                foreach (var k in OptionLabels.Keys) state.SelectedOptions.Add(k);
                break;
            case "none":
                state.SelectedOptions.Clear();
                break;
            case "start":
                updateKeyboard = false;
                if (state.SelectedOptions.Count == 0)
                {
                    await _botClient.AnswerCallbackQuery(query.Id, "⚠️ Оберіть бодай один фільтр!", showAlert: true, cancellationToken: ct);
                    return;
                }
                await _botClient.AnswerCallbackQuery(query.Id, "Запуск кластера...", cancellationToken: ct);
                await _botClient.EditMessageText(
                    chatId, msgId,
                    $"⚙️ <b>GPU ОБРОБКА: АКТИВНО</b>\n\n<blockquote>👤 <code>{state.FileName}</code></blockquote>\n\n📊 <code>[░░░░░░░░░░] 0%</code>\n🔄 <i>Ініціалізація FFmpeg Engine...</i>",
                    ParseMode.Html, cancellationToken: ct);
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
                var bar = new string('█', filled) + new string('░', 10 - filled);
                var text = $"⚙️ <b>GPU ОБРОБКА: В ПРОЦЕСІ</b>\n\n<blockquote>👤 <code>{state.FileName}</code></blockquote>\n\n📊 <code>[{bar}] {pct}%</code>\n🔄 <i>Render Engine (FFmpeg)...</i>";

                if (text == lastText) return;
                lastText = text;
                await _botClient.EditMessageText(chatId, msgId, text, ParseMode.Html, cancellationToken: ct);
            }, ct);

            var finalTxt = $"✅ <b>УНІКАЛІЗАЦІЮ ЗАВЕРШЕНО</b>\n\n" +
                           $"<blockquote>👤 <code>{state.FileName}</code>\n" +
                           $"⚡ Фільтрів застосовано: {state.SelectedOptions.Count}</blockquote>";
                           
            await _botClient.EditMessageText(chatId, msgId, finalTxt, ParseMode.Html, cancellationToken: ct);

            foreach (var res in results)
            {
                var fileName = Path.GetFileName(res) ?? res;
                var baseUrl = _telegramOptions.CurrentValue.BaseUrl.TrimEnd('/');
                var url = $"{baseUrl}/play/{Uri.EscapeDataString(fileName)}";

                var msgText = $"🎬 <b>ГОТОВИЙ ФАЙЛ:</b>\n<code>{fileName}</code>";
                var copyButton = new InlineKeyboardMarkup(
                    [[InlineKeyboardButton.WithCopyText("📋 СКОПІЮВАТИ ПОСИЛАННЯ", url)]]);
                
                await _botClient.SendMessage(chatId, msgText, ParseMode.Html, replyMarkup: copyButton, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for {FileName}.", state.FileName);
            await _botClient.EditMessageText(chatId, msgId, $"❌ <b>CRITICAL FAILURE</b>\n\n<pre>{ex.Message}</pre>", ParseMode.Html, cancellationToken: ct);
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
}
