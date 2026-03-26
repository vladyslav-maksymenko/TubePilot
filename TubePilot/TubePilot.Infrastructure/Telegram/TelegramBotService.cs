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
        { "mirror", "рџЄћ Р”Р·РµСЂРєР°Р»Рѕ (HFlip)" },
        { "reduce_audio", "рџ”‰ Р“СѓС‡РЅС–СЃС‚СЊ -15%" },
        { "slow_down", "рџђЊ Delay 4-7%" },
        { "speed_up", "вљЎ Speed 3-5%" },
        { "color_correct", "рџЋЁ РљРѕСЂРµРєС†С–СЏ РєРѕР»СЊРѕСЂСѓ" },
        { "slice", "вњ‚пёЏ РЁРѕСЂС‚СЃ (2:30-3:10)" },
        { "slice_long", "вњ‚пёЏ Long (5:10-7:10)" },
        { "qr_overlay", "рџ“± Р’С–РґР¶РµС‚ QR" },
        { "rotate", "рџ”„ Р—Р°С…РёСЃРЅРёР№ РїРѕРІРѕСЂРѕС‚" },
        { "downscale_1080p", "рџ“ђ Р”Р°СѓРЅСЃРєРµР№Р» 1080p" }
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
            _logger.LogWarning("РќС–С…С‚Рѕ РЅРµ РїС–РґРїРёСЃР°РЅРёР№ РЅР° Р±РѕС‚Р°! Р—Р°Р№РґС–С‚СЊ Сѓ Telegram С– РЅР°РїРёС€С–С‚СЊ /start РІР°С€РѕРјСѓ Р±РѕС‚Сѓ.");
            return;
        }

        var sizeMb = file.SizeBytes / (1024.0 * 1024.0);
        
        var text = $"рџљЂ <b>Р—РЅР°Р№РґРµРЅРѕ РЅРѕРІРµ РјРµРґС–Р°!</b>\n\n" +
                   $"<blockquote>рџ‘¤ <b>Р¤Р°Р№Р»:</b> <code>{file.Name}</code>\n" +
                   $"рџ’ѕ <b>Р’Р°РіР°:</b> {sizeMb:F1} MB</blockquote>\n\n" +
                   $"рџЋЇ РћР±РµСЂС–С‚СЊ С„С–Р»СЊС‚СЂРё СѓРЅС–РєР°Р»С–Р·Р°С†С–С— Р№ С‚РёСЃРЅС–С‚СЊ <b>РџРѕС‡Р°С‚Рё РѕР±СЂРѕР±РєСѓ</b> рџ‘‡";

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
            var check = isSelected ? "вњ…" : "рџ”";
            buttons.Add([InlineKeyboardButton.WithCallbackData($"{check} {opt.Value}", $"t|{opt.Key}")]);
        }

        buttons.Add([
            InlineKeyboardButton.WithCallbackData("рџ’  Р’РёР±СЂР°С‚Рё РІСЃС–", "all"),
            InlineKeyboardButton.WithCallbackData("вњ–пёЏ РћС‡РёСЃС‚РёС‚Рё", "none")
        ]);

        buttons.Add([InlineKeyboardButton.WithCallbackData("в–¶пёЏ РџРћР§РђРўР РћР‘Р РћР‘РљРЈ", "start")]);

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
                await _botClient.SendMessage(chatId, "в›” Р”РѕСЃС‚СѓРї Р·Р°Р±РѕСЂРѕРЅРµРЅРѕ.", cancellationToken: ct);
                return;
            }

            await File.WriteAllTextAsync(SubscriberFile, chatId.ToString(), ct);
            
            var text = "вњ… <b>РђРІС‚РѕСЂРёР·Р°С†С–СЏ СѓСЃРїС–С€РЅР°!</b>\n\nРўРµРїРµСЂ СЏ Р±СѓРґСѓ РЅР°РґСЃРёР»Р°С‚Рё СЃСЋРґРё С–РЅС‚РµСЂС„РµР№СЃ РґР»СЏ РѕР±СЂРѕР±РєРё РєРѕР¶РЅРѕРіРѕ РЅРѕРІРѕРіРѕ РІС–РґРµРѕ, СЏРєРµ РїРѕС‚СЂР°РїР»СЏС” РЅР° Google Drive рџ›ё";
            await _botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
            
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
            await _botClient.AnswerCallbackQuery(query.Id, "в›” Р”РѕСЃС‚СѓРї Р·Р°Р±РѕСЂРѕРЅРµРЅРѕ.", showAlert: true, cancellationToken: ct);
            return;
        }

        if (!_userSelections.TryGetValue(msgId, out var state))
        {
            await _botClient.AnswerCallbackQuery(query.Id, "вЏі РЎРµСЃС–СЏ Р·Р°СЃС‚Р°СЂС–Р»Р°! Р—Р°РІР°РЅС‚Р°Р¶С‚Рµ РЅРѕРІРµ РІС–РґРµРѕ.", showAlert: true, cancellationToken: ct);
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
                    await _botClient.AnswerCallbackQuery(query.Id, "вљ пёЏ РћР±РµСЂС–С‚СЊ Р±РѕРґР°Р№ РѕРґРёРЅ С„С–Р»СЊС‚СЂ!", showAlert: true, cancellationToken: ct);
                    return;
                }
                await _botClient.AnswerCallbackQuery(query.Id, "Р—Р°РїСѓСЃРє РєР»Р°СЃС‚РµСЂР°...", cancellationToken: ct);
                await _botClient.EditMessageText(
                    chatId, msgId,
                    $"вљ™пёЏ <b>GPU РћР‘Р РћР‘РљРђ: РђРљРўРР’РќРћ</b>\n\n<blockquote>рџ‘¤ <code>{state.FileName}</code></blockquote>\n\nрџ“Љ <code>[в–‘в–‘в–‘в–‘в–‘в–‘в–‘в–‘в–‘в–‘] 0%</code>\nрџ”„ <i>Р†РЅС–С†С–Р°Р»С–Р·Р°С†С–СЏ FFmpeg Engine...</i>",
                    parseMode: ParseMode.Html, cancellationToken: ct);
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
                var text = $"вљ™пёЏ <b>GPU РћР‘Р РћР‘РљРђ: Р’ РџР РћР¦Р•РЎР†</b>\n\n<blockquote>рџ‘¤ <code>{state.FileName}</code></blockquote>\n\nрџ“Љ <code>[{bar}] {pct}%</code>\nрџ”„ <i>Render Engine (FFmpeg)...</i>";

                if (text == lastText) return;
                lastText = text;
                await _botClient.EditMessageText(chatId, msgId, text, parseMode: ParseMode.Html, cancellationToken: ct);
            }, ct);

            var finalTxt = $"вњ… <b>РЈРќР†РљРђР›Р†Р—РђР¦Р†Р® Р—РђР’Р•Р РЁР•РќРћ</b>\n\n" +
                           $"<blockquote>рџ‘¤ <code>{state.FileName}</code>\n" +
                           $"вљЎ Р¤С–Р»СЊС‚СЂС–РІ Р·Р°СЃС‚РѕСЃРѕРІР°РЅРѕ: {state.SelectedOptions.Count}</blockquote>";
                           
            await _botClient.EditMessageText(chatId, msgId, finalTxt, parseMode: ParseMode.Html, cancellationToken: ct);

            foreach (var res in results)
            {
                var fileName = Path.GetFileName(res) ?? res;
                var baseUrl = _telegramOptions.CurrentValue.BaseUrl?.TrimEnd('/') ?? string.Empty;
                var url = $"{baseUrl}/play/{Uri.EscapeDataString(fileName)}";

                if (!IsTelegramSafeButtonUrl(url))
                {
                    var msgTextNoUrl = $"СЂСџР‹В¬ <b>Р вЂњР С›Р СћР С›Р вЂ™Р ВР в„ў Р В¤Р С’Р в„ўР вЂє:</b>\n<code>{fileName}</code>\n\n" +
                                       $"РІС™В РїС‘РЏ <b>Р С™Р Р…Р С•Р С—Р С”Р В°-Р В»РЎвЂ“Р Р…Р С” Р Р…Р Вµ Р Т‘РЎвЂ“РЎвЂќ</b>, Р В±Р С• URL Р Р…Р ВµР С”Р С•РЎР‚Р ВµР С”РЎвЂљР Р…Р С‘Р в„– Р Т‘Р В»РЎРЏ Telegram: <code>{url}</code>\n" +
                                       $"Р вЂ™Р С”Р В°Р В¶Р С‘РЎвЂљРЎРЉ Р С—РЎС“Р В±Р В»РЎвЂ“РЎвЂЎР Р…Р С‘Р в„– https URL РЎС“ <code>Telegram:BaseUrl</code>.";

                    var copyNameButton = new InlineKeyboardMarkup(
                        [[InlineKeyboardButton.WithCopyText("СЂСџвЂњвЂ№ Р РЋР С™Р С›Р СџР вЂ Р В®Р вЂ™Р С’Р СћР В Р СњР С’Р вЂ”Р вЂ™Р Р€ Р В¤Р С’Р в„ўР вЂєР Р€", fileName)]]);

                    await _botClient.SendMessage(chatId, msgTextNoUrl, parseMode: ParseMode.Html, replyMarkup: copyNameButton, cancellationToken: ct);
                    continue;
                }

                var msgText = $"рџЋ¬ <b>Р“РћРўРћР’РР™ Р¤РђР™Р›:</b>\n<code>{fileName}</code>\n\nв–¶пёЏ <a href=\"{url}\">Р”РР’РРўРРЎР¬ Р Р•Р—РЈР›Р¬РўРђРў</a>";
                // Telegram copy-text buttons have strict limits; long URLs (e.g. URL-encoded unicode filenames)
                // can be rejected with BUTTON_COPY_TEXT_INVALID. Fall back to a normal URL button.
                InlineKeyboardMarkup replyMarkup = url.Length <= 256
                    ? new InlineKeyboardMarkup([[InlineKeyboardButton.WithCopyText("рџ“‹ РЎРљРћРџР†Р®Р’РђРўР РџРћРЎРР›РђРќРќРЇ", url)]])
                    : new InlineKeyboardMarkup([[InlineKeyboardButton.WithUrl("рџ”— Р’Р†Р”РљР РРўР Р Р•Р—РЈР›Р¬РўРђРў", url)]]);

                try
                {
                    await _botClient.SendMessage(chatId, msgText, parseMode: ParseMode.Html, replyMarkup: replyMarkup, cancellationToken: ct);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400)
                {
                    if (ex.Message.Contains("BUTTON_COPY_TEXT_INVALID", StringComparison.OrdinalIgnoreCase))
                    {
                        var fallback = new InlineKeyboardMarkup([[InlineKeyboardButton.WithUrl("рџ”— Р’Р†Р”РљР РРўР Р Р•Р—РЈР›Р¬РўРђРў", url)]]);
                        try
                        {
                            await _botClient.SendMessage(chatId, msgText, parseMode: ParseMode.Html, replyMarkup: fallback, cancellationToken: ct);
                        }
                        catch (ApiRequestException innerEx) when (innerEx.ErrorCode == 400)
                        {
                            await _botClient.SendMessage(chatId, msgText, parseMode: ParseMode.Html, cancellationToken: ct);
                            _logger.LogWarning(innerEx, "Telegram rejected fallback inline keyboard for URL: {Url}", url);
                        }

                        continue;
                    }

                    await _botClient.SendMessage(chatId, msgText, parseMode: ParseMode.Html, cancellationToken: ct);
                    _logger.LogWarning(ex, "Telegram rejected inline keyboard for URL: {Url}", url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for {FileName}.", state.FileName);
            await _botClient.EditMessageText(chatId, msgId, $"вќЊ <b>CRITICAL FAILURE</b>\n\n<pre>{ex.Message}</pre>", parseMode: ParseMode.Html, cancellationToken: ct);
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

        // Telegram commonly rejects loopback hosts ("localhost", "127.0.0.1") in URL buttons.
        if (uri.IsLoopback)
        {
            return false;
        }

        // Additional guard for "0.0.0.0" etc.
        if (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))
        {
            return false;
        }

        return true;
    }
}
