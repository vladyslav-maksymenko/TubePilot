using System.Net;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TubePilot.Core.Contracts;
using TubePilot.Core.Domain;
using TubePilot.Infrastructure.YouTube;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramChannelManagementHandler(
    ITelegramUiClient ui,
    IChannelStore channelStore,
    IOAuthCodeExchanger oAuthCodeExchanger,
    IYouTubeChannelLookup youTubeChannelLookup,
    ILogger<TelegramChannelManagementHandler> logger)
{
    internal const string Prefix = "ch:";

    private const string YouTubeUploadScope = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";
    private const string OAuthRedirectUri = "urn:ietf:wg:oauth:2.0:oob";

    private static readonly ReplyKeyboardMarkup DefaultKeyboard = new(
    [
        [new KeyboardButton("📋 Мої групи каналів"), new KeyboardButton("➕ Додати групу")],
        [new KeyboardButton("❓ Допомога")]
    ])
    {
        ResizeKeyboard = true,
        IsPersistent = true
    };

    // Wizard sessions per chat
    private readonly Dictionary<long, ChannelWizardSession> _wizards = [];

    // Track which group the user is currently viewing (for contextual reply keyboard)
    private readonly Dictionary<long, string> _lastViewedGroupId = [];

    internal bool HasActiveWizard(long chatId) => _wizards.ContainsKey(chatId);
    internal string? GetLastViewedGroupId(long chatId) => _lastViewedGroupId.GetValueOrDefault(chatId);

    // ── Main menu ──

    internal async Task ShowMainMenuAsync(long chatId, CancellationToken ct)
    {
        _lastViewedGroupId.Remove(chatId);
        await ui.SendMessageWithReplyKeyboardAsync(chatId, "🏠 Головне меню", DefaultKeyboard, ct: ct);

        var groups = channelStore.GetAllGroups();
        if (groups.Count == 0)
        {
            await ui.SendMessageAsync(chatId,
                "📋 <b>Мої групи каналів</b>\n\nПоки немає жодної групи. Додай першу!",
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup([
                    [InlineKeyboardButton.WithCallbackData("➕ Додати групу", $"{Prefix}add-group")]
                ]), ct: ct);
            return;
        }

        var lines = new List<string> { "📋 <b>Мої групи каналів</b>\n" };
        var buttons = new List<InlineKeyboardButton[]>();

        foreach (var g in groups)
        {
            channelStore.ResetQuotaIfNeeded(g.Id);
            var remaining = channelStore.GetRemainingQuota(g.Id);
            var uploadsUsed = (int)(g.QuotaUsedToday / 1650);
            var status = remaining >= 1650 ? "🟢" : "🔴";
            var connectedCount = g.Channels.Count(c => !string.IsNullOrWhiteSpace(c.RefreshToken));
            var tokenIcon = connectedCount == g.Channels.Count && g.Channels.Count > 0 ? "🔑" : "⚠️";

            lines.Add($"{status} <b>{H(g.Name)}</b> {tokenIcon}");
            lines.Add($"   📺 {connectedCount}/{g.Channels.Count} підключено · 📤 {uploadsUsed}/~6 сьогодні");

            buttons.Add([InlineKeyboardButton.WithCallbackData($"📂 {g.Name}", $"{Prefix}group:{g.Id}")]);
        }

        buttons.Add([InlineKeyboardButton.WithCallbackData("➕ Додати групу", $"{Prefix}add-group")]);

        await ui.SendMessageAsync(chatId,
            string.Join('\n', lines),
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(buttons), ct: ct);
    }

    // ── Group detail ──

    internal async Task ShowGroupDetailAsync(long chatId, string groupId, CancellationToken ct)
    {
        var group = channelStore.GetGroup(groupId);
        if (group is null)
        {
            await ui.SendMessageAsync(chatId, "❌ Групу не знайдено.", ct: ct);
            return;
        }

        _lastViewedGroupId[chatId] = groupId;
        var groupKeyboard = new ReplyKeyboardMarkup(
        [
            [new KeyboardButton("📋 Мої групи каналів"), new KeyboardButton("➕ Додати канал")],
            [new KeyboardButton("❓ Допомога")]
        ])
        {
            ResizeKeyboard = true,
            IsPersistent = true
        };
        await ui.SendMessageWithReplyKeyboardAsync(chatId, $"📂 Група: {group.Name}", groupKeyboard, ct: ct);

        channelStore.ResetQuotaIfNeeded(groupId);
        var remaining = channelStore.GetRemainingQuota(groupId);
        var connectedCount = group.Channels.Count(c => !string.IsNullOrWhiteSpace(c.RefreshToken));

        var lines = new List<string>
        {
            $"📂 <b>{H(group.Name)}</b>\n",
            $"🔑 OAuth: {connectedCount}/{group.Channels.Count} каналів підключено",
            $"📊 Квота: <code>{group.QuotaUsedToday:0}/{10000}</code> ({remaining:0} залишилось)",
            $"📺 Каналів: {group.Channels.Count}",
        };

        if (group.Channels.Count > 0)
        {
            lines.Add("");
            foreach (var ch in group.Channels)
            {
                var tokenStatus = !string.IsNullOrWhiteSpace(ch.RefreshToken) ? "🔑" : "⚠️";
                var folder = string.IsNullOrWhiteSpace(ch.DriveFolderId) ? "—" : ch.DriveFolderId[..Math.Min(12, ch.DriveFolderId.Length)] + "…";
                lines.Add($"  {tokenStatus} <b>{H(ch.Name)}</b> → 📁 <code>{folder}</code>");
            }
        }

        var buttons = new List<InlineKeyboardButton[]>();

        // Channel management
        foreach (var ch in group.Channels)
        {
            buttons.Add([InlineKeyboardButton.WithCallbackData($"✏️ {ch.Name}", $"{Prefix}edit-ch:{groupId}:{ch.Id}")]);
        }

        buttons.Add([InlineKeyboardButton.WithCallbackData("➕ Додати канал", $"{Prefix}add-ch:{groupId}")]);
        buttons.Add([
            InlineKeyboardButton.WithCallbackData("✏️ Назва", $"{Prefix}rename-group:{groupId}"),
        ]);
        buttons.Add([
            InlineKeyboardButton.WithCallbackData("🗑 Видалити групу", $"{Prefix}del-group:{groupId}"),
            InlineKeyboardButton.WithCallbackData("◀️ Назад", $"{Prefix}main")
        ]);

        await ui.SendMessageAsync(chatId,
            string.Join('\n', lines),
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(buttons), ct: ct);
    }

    // ── Channel detail / edit ──

    internal async Task ShowChannelEditAsync(long chatId, string groupId, string channelId, CancellationToken ct)
    {
        var group = channelStore.GetGroup(groupId);
        var channel = group?.Channels.FirstOrDefault(c => c.Id == channelId);
        if (group is null || channel is null)
        {
            await ui.SendMessageAsync(chatId, "❌ Канал не знайдено.", ct: ct);
            return;
        }

        var ytId = string.IsNullOrWhiteSpace(channel.YouTubeChannelId) ? "—" : channel.YouTubeChannelId;
        var folder = string.IsNullOrWhiteSpace(channel.DriveFolderId) ? "—" : channel.DriveFolderId;
        var tokenStatus = !string.IsNullOrWhiteSpace(channel.RefreshToken) ? "✅ Підключено" : "⚠️ Не підключено";

        var text = $"📺 <b>{H(channel.Name)}</b>\n\n" +
                   $"🔑 OAuth: {tokenStatus}\n" +
                   $"🆔 YouTube ID: <code>{H(ytId)}</code>\n" +
                   $"📁 Drive Folder: <code>{H(folder)}</code>\n" +
                   $"📂 Група: {H(group.Name)}";

        var buttons = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("🔑 Підключити OAuth", $"{Prefix}oauth:{groupId}:{channelId}") },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Назва", $"{Prefix}rename-ch:{groupId}:{channelId}") },
            new[] { InlineKeyboardButton.WithCallbackData("🆔 YouTube ID", $"{Prefix}set-ytid:{groupId}:{channelId}") },
            new[] { InlineKeyboardButton.WithCallbackData("📁 Drive Folder", $"{Prefix}set-folder:{groupId}:{channelId}") },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Видалити", $"{Prefix}del-ch:{groupId}:{channelId}"),
                InlineKeyboardButton.WithCallbackData("◀️ Назад", $"{Prefix}group:{groupId}")
            }
        };

        await ui.SendMessageAsync(chatId, text, parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(buttons), ct: ct);
    }

    // ── Callback router ──

    internal async Task HandleCallbackAsync(long chatId, string action, CancellationToken ct)
    {
        if (action == "main")
        {
            _wizards.Remove(chatId);
            await ShowMainMenuAsync(chatId, ct);
            return;
        }

        if (action == "add-group")
        {
            _wizards[chatId] = new ChannelWizardSession { Step = WizardStep.EnterGroupName };
            await ui.SendMessageAsync(chatId, "✏️ Введи назву нової групи:", ct: ct);
            return;
        }

        if (action.StartsWith("group:", StringComparison.Ordinal))
        {
            var groupId = action["group:".Length..];
            await ShowGroupDetailAsync(chatId, groupId, ct);
            return;
        }

        if (action.StartsWith("add-ch:", StringComparison.Ordinal))
        {
            var groupId = action["add-ch:".Length..];
            var group = channelStore.GetGroup(groupId);
            if (group is null)
            {
                await ui.SendMessageAsync(chatId, "❌ Групу не знайдено.", ct: ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(group.ClientId) || string.IsNullOrWhiteSpace(group.ClientSecret))
            {
                await ui.SendMessageAsync(chatId, "⚠️ Для цієї групи не налаштовано Client ID / Client Secret.", ct: ct);
                return;
            }

            var oauthUrl = BuildOAuthUrl(group.ClientId);

            _wizards[chatId] = new ChannelWizardSession
            {
                Step = WizardStep.AddChannelOAuthCode,
                GroupId = groupId
            };

            await ui.SendMessageAsync(chatId,
                "📺 <b>Додавання каналу</b>\n\n" +
                "1️⃣ Відкрий посилання у своєму браузері\n" +
                "2️⃣ Увійди в Gmail → обери канал → натисни «Дозволити»\n" +
                "3️⃣ Google покаже код — скопіюй його і відправ сюди\n\n" +
                "📋 <b>Скопіювати посилання:</b>\n" +
                $"<code>{H(oauthUrl)}</code>",
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup([
                    [InlineKeyboardButton.WithUrl("🌐 Відкрити в браузері", oauthUrl)]
                ]),
                ct: ct);
            return;
        }

        if (action.StartsWith("edit-ch:", StringComparison.Ordinal))
        {
            var parts = action["edit-ch:".Length..].Split(':');
            if (parts.Length == 2)
                await ShowChannelEditAsync(chatId, parts[0], parts[1], ct);
            return;
        }

        if (action.StartsWith("rename-group:", StringComparison.Ordinal))
        {
            var groupId = action["rename-group:".Length..];
            _wizards[chatId] = new ChannelWizardSession { Step = WizardStep.RenameGroup, GroupId = groupId };
            await ui.SendMessageAsync(chatId, "✏️ Введи нову назву групи:", ct: ct);
            return;
        }

        if (action.StartsWith("oauth:", StringComparison.Ordinal))
        {
            var parts = action["oauth:".Length..].Split(':');
            if (parts.Length == 2)
            {
                var groupId = parts[0];
                var channelId = parts[1];
                await StartOAuthFlowAsync(chatId, groupId, channelId, ct);
            }
            return;
        }

        if (action.StartsWith("rename-ch:", StringComparison.Ordinal))
        {
            var parts = action["rename-ch:".Length..].Split(':');
            if (parts.Length == 2)
            {
                _wizards[chatId] = new ChannelWizardSession { Step = WizardStep.RenameChannel, GroupId = parts[0], ChannelId = parts[1] };
                await ui.SendMessageAsync(chatId, "✏️ Введи нову назву каналу:", ct: ct);
            }
            return;
        }

        if (action.StartsWith("set-ytid:", StringComparison.Ordinal))
        {
            var parts = action["set-ytid:".Length..].Split(':');
            if (parts.Length == 2)
            {
                _wizards[chatId] = new ChannelWizardSession { Step = WizardStep.SetYouTubeId, GroupId = parts[0], ChannelId = parts[1] };
                await ui.SendMessageAsync(chatId, "🆔 Введи YouTube Channel ID (UC...):", ct: ct);
            }
            return;
        }

        if (action.StartsWith("set-folder:", StringComparison.Ordinal))
        {
            var parts = action["set-folder:".Length..].Split(':');
            if (parts.Length == 2)
            {
                _wizards[chatId] = new ChannelWizardSession { Step = WizardStep.SetDriveFolder, GroupId = parts[0], ChannelId = parts[1] };
                await ui.SendMessageAsync(chatId, "📁 Введи Google Drive Folder ID:", ct: ct);
            }
            return;
        }

        if (action.StartsWith("del-group:", StringComparison.Ordinal))
        {
            var groupId = action["del-group:".Length..];
            var group = channelStore.GetGroup(groupId);
            if (group is null)
            {
                await ShowMainMenuAsync(chatId, ct);
                return;
            }

            var channelCount = group.Channels.Count;
            _wizards[chatId] = new ChannelWizardSession { Step = WizardStep.ConfirmDeleteGroup, GroupId = groupId };

            await ui.SendMessageAsync(chatId,
                $"⚠️ <b>Видалити групу «{H(group.Name)}»?</b>\n\n" +
                $"В групі {channelCount} канал(ів).\n" +
                "Після видалення дані будуть втрачені безповоротно.\n\n" +
                "Напиши <code>Так, я хочу видалити</code> щоб підтвердити або /cancel щоб скасувати.",
                parseMode: ParseMode.Html, ct: ct);
            return;
        }

        if (action.StartsWith("del-ch:", StringComparison.Ordinal))
        {
            var parts = action["del-ch:".Length..].Split(':');
            if (parts.Length != 2) return;

            var groupId = parts[0];
            var channelId = parts[1];
            var ch = channelStore.GetChannel(channelId);
            if (ch is null)
            {
                await ShowGroupDetailAsync(chatId, groupId, ct);
                return;
            }

            var text = $"⚠️ <b>Видалити канал «{H(ch.Name)}»?</b>\n\n" +
                       "Після видалення дані будуть втрачені безповоротно.";

            var buttons = new InlineKeyboardMarkup([
                [
                    InlineKeyboardButton.WithCallbackData("✅ Так, видалити", $"{Prefix}confirm-del-ch:{groupId}:{channelId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"{Prefix}edit-ch:{groupId}:{channelId}")
                ]
            ]);

            await ui.SendMessageAsync(chatId, text, parseMode: ParseMode.Html, replyMarkup: buttons, ct: ct);
            return;
        }

        if (action.StartsWith("confirm-del-ch:", StringComparison.Ordinal))
        {
            var parts = action["confirm-del-ch:".Length..].Split(':');
            if (parts.Length == 2)
            {
                var ch = channelStore.GetChannel(parts[1]);
                channelStore.RemoveChannel(parts[0], parts[1]);
                if (ch is not null)
                    await ui.SendMessageAsync(chatId, $"🗑 Канал <b>{H(ch.Name)}</b> видалено.", parseMode: ParseMode.Html, ct: ct);
                await ShowGroupDetailAsync(chatId, parts[0], ct);
            }
            return;
        }
    }

    // ── OAuth flow ──

    private async Task StartOAuthFlowAsync(long chatId, string groupId, string channelId, CancellationToken ct)
    {
        var group = channelStore.GetGroup(groupId);
        var channel = group?.Channels.FirstOrDefault(c => c.Id == channelId);
        if (group is null || channel is null)
        {
            await ui.SendMessageAsync(chatId, "❌ Канал не знайдено.", ct: ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(group.ClientId) || string.IsNullOrWhiteSpace(group.ClientSecret))
        {
            await ui.SendMessageAsync(chatId, "⚠️ Для цієї групи не налаштовано Client ID / Client Secret.", ct: ct);
            return;
        }

        var oauthUrl = BuildOAuthUrl(group.ClientId);

        _wizards[chatId] = new ChannelWizardSession
        {
            Step = WizardStep.EnterAuthorizationCode,
            GroupId = groupId,
            ChannelId = channelId
        };

        await ui.SendMessageAsync(chatId,
            $"🔑 <b>Підключення каналу «{H(channel.Name)}»</b>\n\n" +
            "1️⃣ Відкрий посилання у своєму браузері\n" +
            "2️⃣ Увійди в Gmail → обери канал → натисни «Дозволити»\n" +
            "3️⃣ Google покаже код — скопіюй його і відправ сюди\n\n" +
            "📋 <b>Скопіювати посилання:</b>\n" +
            $"<code>{H(oauthUrl)}</code>",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup([
                [InlineKeyboardButton.WithUrl("🌐 Відкрити в браузері", oauthUrl)]
            ]),
            ct: ct);
    }

    private static string BuildOAuthUrl(string clientId)
    {
        return "https://accounts.google.com/o/oauth2/v2/auth" +
               $"?client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(OAuthRedirectUri)}" +
               "&response_type=code" +
               $"&scope={Uri.EscapeDataString(YouTubeUploadScope)}" +
               "&access_type=offline" +
               "&prompt=consent";
    }

    // ── Text input handler ──

    internal async Task<bool> HandleTextAsync(long chatId, string text, CancellationToken ct)
    {
        if (!_wizards.TryGetValue(chatId, out var wizard))
            return false;

        switch (wizard.Step)
        {
            case WizardStep.EnterGroupName:
                wizard.GroupName = text.Trim();
                wizard.Step = WizardStep.EnterClientId;
                await ui.SendMessageAsync(chatId, "🔑 Введи Client ID (з GCP Console):", ct: ct);
                return true;

            case WizardStep.EnterClientId:
                wizard.ClientId = text.Trim();
                wizard.Step = WizardStep.EnterClientSecret;
                await ui.SendMessageAsync(chatId, "🔐 Введи Client Secret:", ct: ct);
                return true;

            case WizardStep.EnterClientSecret:
            {
                wizard.ClientSecret = text.Trim();

                // Create group immediately (no more RefreshToken step for group)
                var newGroup = new GmailGroup
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = wizard.GroupName ?? "Unnamed",
                    ClientId = wizard.ClientId ?? "",
                    ClientSecret = wizard.ClientSecret,
                    QuotaResetAtUtc = DateTimeOffset.UtcNow,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                channelStore.AddGroup(newGroup);
                _wizards.Remove(chatId);
                await ui.SendMessageAsync(chatId,
                    $"✅ Групу <b>{H(newGroup.Name)}</b> створено!\n\n" +
                    "Тепер додай канали і підключи OAuth для кожного.",
                    parseMode: ParseMode.Html, ct: ct);
                await ShowGroupDetailAsync(chatId, newGroup.Id, ct);
                return true;
            }

            case WizardStep.EnterAuthorizationCode:
            {
                var code = text.Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    await ui.SendMessageAsync(chatId, "⚠️ Код не може бути порожнім. Спробуй ще раз:", ct: ct);
                    return true;
                }

                var group = channelStore.GetGroup(wizard.GroupId!);
                var channel = group?.Channels.FirstOrDefault(c => c.Id == wizard.ChannelId);
                if (group is null || channel is null)
                {
                    await ui.SendMessageAsync(chatId, "❌ Канал не знайдено.", ct: ct);
                    _wizards.Remove(chatId);
                    return true;
                }

                await ui.SendMessageAsync(chatId, "⏳ Обмінюю код на токен...", ct: ct);

                try
                {
                    var result = await oAuthCodeExchanger.ExchangeCodeAsync(
                        code, group.ClientId, group.ClientSecret, OAuthRedirectUri, ct);

                    channel.RefreshToken = result.RefreshToken;

                    // Auto-fetch YouTube Channel ID and name
                    if (!string.IsNullOrWhiteSpace(result.AccessToken))
                    {
                        try
                        {
                            var ytChannels = await youTubeChannelLookup.GetChannelsAsync(result.AccessToken, ct);
                            if (ytChannels.Count > 0)
                            {
                                channel.YouTubeChannelId = ytChannels[0].Id;
                                channel.Name = ytChannels[0].Title;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Failed to auto-fetch YouTube channel info, skipping.");
                        }
                    }

                    channelStore.UpdateChannel(group.Id, channel);

                    logger.LogInformation("OAuth token saved for channel {ChannelName} (YT: {YouTubeId}) in group {GroupName}",
                        channel.Name, channel.YouTubeChannelId ?? "unknown", group.Name);

                    _wizards.Remove(chatId);
                    var ytIdLine = !string.IsNullOrWhiteSpace(channel.YouTubeChannelId)
                        ? $"\n🆔 YouTube ID: <code>{H(channel.YouTubeChannelId)}</code>"
                        : "";
                    await ui.SendMessageAsync(chatId,
                        $"✅ Канал <b>{H(channel.Name)}</b> підключено!{ytIdLine}",
                        parseMode: ParseMode.Html, ct: ct);
                    await ShowChannelEditAsync(chatId, group.Id, channel.Id, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "OAuth code exchange failed for channel {ChannelId}", wizard.ChannelId);
                    await ui.SendMessageAsync(chatId,
                        $"❌ Помилка обміну коду: <code>{H(ex.Message)}</code>\n\nСпробуй ще раз — скопіюй новий код:",
                        parseMode: ParseMode.Html, ct: ct);
                }
                return true;
            }

            case WizardStep.AddChannelOAuthCode:
            {
                var code = text.Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    await ui.SendMessageAsync(chatId, "⚠️ Код не може бути порожнім. Спробуй ще раз:", ct: ct);
                    return true;
                }

                var group = channelStore.GetGroup(wizard.GroupId!);
                if (group is null)
                {
                    await ui.SendMessageAsync(chatId, "❌ Групу не знайдено.", ct: ct);
                    _wizards.Remove(chatId);
                    return true;
                }

                await ui.SendMessageAsync(chatId, "⏳ Обмінюю код на токен...", ct: ct);

                try
                {
                    var result = await oAuthCodeExchanger.ExchangeCodeAsync(
                        code, group.ClientId, group.ClientSecret, OAuthRedirectUri, ct);

                    // Create channel with token
                    var channel = new YouTubeChannel
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Name = "New Channel",
                        RefreshToken = result.RefreshToken,
                        CreatedAtUtc = DateTimeOffset.UtcNow
                    };

                    // Auto-fetch YouTube Channel ID and name
                    if (!string.IsNullOrWhiteSpace(result.AccessToken))
                    {
                        try
                        {
                            var ytChannels = await youTubeChannelLookup.GetChannelsAsync(result.AccessToken, ct);
                            if (ytChannels.Count > 0)
                            {
                                channel.YouTubeChannelId = ytChannels[0].Id;
                                channel.Name = ytChannels[0].Title;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Failed to auto-fetch YouTube channel info.");
                        }
                    }

                    channelStore.AddChannel(group.Id, channel);
                    wizard.NewChannelId = channel.Id;
                    wizard.ChannelId = channel.Id;

                    logger.LogInformation("New channel added via OAuth: {ChannelName} (YT: {YouTubeId}) in group {GroupName}",
                        channel.Name, channel.YouTubeChannelId ?? "unknown", group.Name);

                    // Ask to confirm or change name
                    wizard.Step = WizardStep.AddChannelConfirmName;
                    var ytIdLine = !string.IsNullOrWhiteSpace(channel.YouTubeChannelId)
                        ? $"\n🆔 <code>{H(channel.YouTubeChannelId)}</code>"
                        : "";

                    await ui.SendMessageAsync(chatId,
                        $"✅ OAuth пройдено!\n\n" +
                        $"📺 Канал: <b>{H(channel.Name)}</b>{ytIdLine}\n\n" +
                        "Залишити цю назву чи ввести свій аліас?\n" +
                        "Натисни /skip щоб залишити або введи нову назву:",
                        parseMode: ParseMode.Html, ct: ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "OAuth code exchange failed during add-channel flow");
                    await ui.SendMessageAsync(chatId,
                        $"❌ Помилка обміну коду: <code>{H(ex.Message)}</code>\n\nСпробуй ще раз — скопіюй новий код:",
                        parseMode: ParseMode.Html, ct: ct);
                }
                return true;
            }

            case WizardStep.AddChannelConfirmName:
            {
                var input = text.Trim();
                var isSkip = input.Equals("/skip", StringComparison.OrdinalIgnoreCase);

                if (!isSkip && !string.IsNullOrWhiteSpace(input))
                {
                    var ch = channelStore.GetChannel(wizard.NewChannelId!);
                    if (ch is not null)
                    {
                        ch.Name = input;
                        channelStore.UpdateChannel(wizard.GroupId!, ch);
                    }
                }

                wizard.Step = WizardStep.AddChannelDriveFolder;
                await ui.SendMessageAsync(chatId,
                    "📁 Введи Google Drive Folder ID для цього каналу:\n\n" +
                    "<i>/skip щоб додати пізніше</i>",
                    parseMode: ParseMode.Html, ct: ct);
                return true;
            }

            case WizardStep.AddChannelDriveFolder:
            {
                var input = text.Trim();
                var isSkip = input.Equals("/skip", StringComparison.OrdinalIgnoreCase);

                if (!isSkip && !string.IsNullOrWhiteSpace(input))
                {
                    var ch = channelStore.GetChannel(wizard.NewChannelId!);
                    if (ch is not null)
                    {
                        ch.DriveFolderId = input;
                        channelStore.UpdateChannel(wizard.GroupId!, ch);
                    }
                }

                var channel = channelStore.GetChannel(wizard.NewChannelId!);
                _wizards.Remove(chatId);
                await ui.SendMessageAsync(chatId,
                    $"✅ Канал <b>{H(channel?.Name ?? "")}</b> додано та підключено!",
                    parseMode: ParseMode.Html, ct: ct);
                await ShowGroupDetailAsync(chatId, wizard.GroupId!, ct);
                return true;
            }

            case WizardStep.EnterChannelName:
            {
                var channel = new YouTubeChannel
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = text.Trim(),
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                channelStore.AddChannel(wizard.GroupId!, channel);
                _wizards.Remove(chatId);
                await ui.SendMessageAsync(chatId, $"✅ Канал <b>{H(channel.Name)}</b> додано!", parseMode: ParseMode.Html, ct: ct);
                await ShowGroupDetailAsync(chatId, wizard.GroupId!, ct);
                return true;
            }

            case WizardStep.RenameGroup:
            {
                var group = channelStore.GetGroup(wizard.GroupId!);
                if (group is not null)
                {
                    group.Name = text.Trim();
                    channelStore.UpdateGroup(group);
                    await ui.SendMessageAsync(chatId, $"✅ Групу перейменовано на <b>{H(group.Name)}</b>", parseMode: ParseMode.Html, ct: ct);
                }
                _wizards.Remove(chatId);
                await ShowGroupDetailAsync(chatId, wizard.GroupId!, ct);
                return true;
            }

            case WizardStep.RenameChannel:
            {
                var ch = channelStore.GetChannel(wizard.ChannelId!);
                if (ch is not null)
                {
                    ch.Name = text.Trim();
                    channelStore.UpdateChannel(wizard.GroupId!, ch);
                    await ui.SendMessageAsync(chatId, $"✅ Канал перейменовано на <b>{H(ch.Name)}</b>", parseMode: ParseMode.Html, ct: ct);
                }
                _wizards.Remove(chatId);
                await ShowChannelEditAsync(chatId, wizard.GroupId!, wizard.ChannelId!, ct);
                return true;
            }

            case WizardStep.SetYouTubeId:
            {
                var ch = channelStore.GetChannel(wizard.ChannelId!);
                if (ch is not null)
                {
                    ch.YouTubeChannelId = text.Trim();
                    channelStore.UpdateChannel(wizard.GroupId!, ch);
                    await ui.SendMessageAsync(chatId, $"✅ YouTube ID встановлено: <code>{H(ch.YouTubeChannelId)}</code>", parseMode: ParseMode.Html, ct: ct);
                }
                _wizards.Remove(chatId);
                await ShowChannelEditAsync(chatId, wizard.GroupId!, wizard.ChannelId!, ct);
                return true;
            }

            case WizardStep.SetDriveFolder:
            {
                var ch = channelStore.GetChannel(wizard.ChannelId!);
                if (ch is not null)
                {
                    ch.DriveFolderId = text.Trim();
                    channelStore.UpdateChannel(wizard.GroupId!, ch);
                    await ui.SendMessageAsync(chatId, $"✅ Drive Folder встановлено: <code>{H(ch.DriveFolderId)}</code>", parseMode: ParseMode.Html, ct: ct);
                }
                _wizards.Remove(chatId);
                await ShowChannelEditAsync(chatId, wizard.GroupId!, wizard.ChannelId!, ct);
                return true;
            }

            case WizardStep.ConfirmDeleteGroup:
            {
                if (text.Trim().Equals("Так, я хочу видалити", StringComparison.OrdinalIgnoreCase))
                {
                    var group = channelStore.GetGroup(wizard.GroupId!);
                    if (group is not null)
                    {
                        channelStore.RemoveGroup(wizard.GroupId!);
                        await ui.SendMessageAsync(chatId, $"🗑 Групу <b>{H(group.Name)}</b> видалено.", parseMode: ParseMode.Html, ct: ct);
                    }
                    _wizards.Remove(chatId);
                    await ShowMainMenuAsync(chatId, ct);
                }
                else
                {
                    _wizards.Remove(chatId);
                    await ui.SendMessageAsync(chatId, "❌ Видалення скасовано.", ct: ct);
                    await ShowGroupDetailAsync(chatId, wizard.GroupId!, ct);
                }
                return true;
            }

            default:
                _wizards.Remove(chatId);
                return false;
        }
    }

    internal void CancelWizard(long chatId) => _wizards.Remove(chatId);

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private enum WizardStep
    {
        EnterGroupName,
        EnterClientId,
        EnterClientSecret,
        EnterAuthorizationCode,   // OAuth for existing channel (re-connect)
        AddChannelOAuthCode,      // OAuth for new channel (add flow)
        AddChannelConfirmName,    // Confirm or change auto-detected name
        AddChannelDriveFolder,    // Enter Drive Folder ID or /skip
        EnterChannelName,         // Legacy: manual channel name entry
        ConfirmDeleteGroup,
        RenameGroup,
        RenameChannel,
        SetYouTubeId,
        SetDriveFolder
    }

    private sealed class ChannelWizardSession
    {
        public WizardStep Step { get; set; }
        public string? GroupId { get; set; }
        public string? ChannelId { get; set; }
        public string? GroupName { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        // Used during add-channel flow to hold the newly created channel
        public string? NewChannelId { get; set; }
    }
}
