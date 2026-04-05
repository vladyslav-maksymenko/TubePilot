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
    ILogger<TelegramChannelManagementHandler> logger)
{
    internal const string Prefix = "ch:";

    // Wizard sessions per chat
    private readonly Dictionary<long, ChannelWizardSession> _wizards = [];

    internal bool HasActiveWizard(long chatId) => _wizards.ContainsKey(chatId);

    // ── Main menu ──

    internal async Task ShowMainMenuAsync(long chatId, CancellationToken ct)
    {
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
            var uploadsLeft = (int)(remaining / 1650);
            var uploadsUsed = (int)(g.QuotaUsedToday / 1650);
            var status = remaining >= 1650 ? "🟢" : "🔴";
            var hasToken = !string.IsNullOrWhiteSpace(g.RefreshToken);
            var tokenIcon = hasToken ? "🔑" : "⚠️";

            lines.Add($"{status} <b>{H(g.Name)}</b> {tokenIcon}");
            lines.Add($"   📺 {g.Channels.Count} каналів · 📤 {uploadsUsed}/~6 сьогодні");

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

        channelStore.ResetQuotaIfNeeded(groupId);
        var remaining = channelStore.GetRemainingQuota(groupId);
        var hasToken = !string.IsNullOrWhiteSpace(group.RefreshToken);

        var lines = new List<string>
        {
            $"📂 <b>{H(group.Name)}</b>\n",
            $"🔑 OAuth: {(hasToken ? "✅ Підключено" : "⚠️ Не підключено")}",
            $"📊 Квота: <code>{group.QuotaUsedToday:0}/{10000}</code> ({remaining:0} залишилось)",
            $"📺 Каналів: {group.Channels.Count}",
        };

        if (group.Channels.Count > 0)
        {
            lines.Add("");
            foreach (var ch in group.Channels)
            {
                var folder = string.IsNullOrWhiteSpace(ch.DriveFolderId) ? "—" : ch.DriveFolderId[..Math.Min(12, ch.DriveFolderId.Length)] + "…";
                lines.Add($"  • <b>{H(ch.Name)}</b> → 📁 <code>{folder}</code>");
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
            InlineKeyboardButton.WithCallbackData("🔑 Токен", $"{Prefix}set-token:{groupId}")
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

        var text = $"📺 <b>{H(channel.Name)}</b>\n\n" +
                   $"🆔 YouTube ID: <code>{H(ytId)}</code>\n" +
                   $"📁 Drive Folder: <code>{H(folder)}</code>\n" +
                   $"📂 Група: {H(group.Name)}";

        var buttons = new List<InlineKeyboardButton[]>
        {
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
            _wizards[chatId] = new ChannelWizardSession { Step = WizardStep.EnterChannelName, GroupId = groupId };
            await ui.SendMessageAsync(chatId, "✏️ Введи назву нового каналу:", ct: ct);
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

        if (action.StartsWith("set-token:", StringComparison.Ordinal))
        {
            var groupId = action["set-token:".Length..];
            _wizards[chatId] = new ChannelWizardSession { Step = WizardStep.EnterRefreshToken, GroupId = groupId };
            await ui.SendMessageAsync(chatId,
                "🔑 Введи Refresh Token для цієї групи:\n\n" +
                "<i>Отримай через OAuth flow (get_token.py) або /skip щоб пропустити</i>",
                parseMode: ParseMode.Html, ct: ct);
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
            if (group is not null)
            {
                channelStore.RemoveGroup(groupId);
                await ui.SendMessageAsync(chatId, $"🗑 Групу <b>{H(group.Name)}</b> видалено.", parseMode: ParseMode.Html, ct: ct);
            }
            await ShowMainMenuAsync(chatId, ct);
            return;
        }

        if (action.StartsWith("del-ch:", StringComparison.Ordinal))
        {
            var parts = action["del-ch:".Length..].Split(':');
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
                wizard.ClientSecret = text.Trim();
                wizard.Step = WizardStep.EnterRefreshToken;
                await ui.SendMessageAsync(chatId,
                    "🔑 Введи Refresh Token:\n\n<i>/skip щоб додати пізніше</i>",
                    parseMode: ParseMode.Html, ct: ct);
                return true;

            case WizardStep.EnterRefreshToken:
            {
                var token = text.Trim();
                var isSkip = token.Equals("/skip", StringComparison.OrdinalIgnoreCase);

                if (wizard.GroupId is not null)
                {
                    // Updating existing group token
                    var group = channelStore.GetGroup(wizard.GroupId);
                    if (group is not null && !isSkip)
                    {
                        group.RefreshToken = token;
                        channelStore.UpdateGroup(group);
                        await ui.SendMessageAsync(chatId, "✅ Токен оновлено!", ct: ct);
                    }
                    _wizards.Remove(chatId);
                    if (group is not null)
                        await ShowGroupDetailAsync(chatId, group.Id, ct);
                    else
                        await ShowMainMenuAsync(chatId, ct);
                    return true;
                }

                // Creating new group
                var newGroup = new GmailGroup
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = wizard.GroupName ?? "Unnamed",
                    ClientId = wizard.ClientId ?? "",
                    ClientSecret = wizard.ClientSecret ?? "",
                    RefreshToken = isSkip ? null : token,
                    QuotaResetAtUtc = DateTimeOffset.UtcNow,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                channelStore.AddGroup(newGroup);
                _wizards.Remove(chatId);
                await ui.SendMessageAsync(chatId, $"✅ Групу <b>{H(newGroup.Name)}</b> створено!", parseMode: ParseMode.Html, ct: ct);
                await ShowGroupDetailAsync(chatId, newGroup.Id, ct);
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
        EnterRefreshToken,
        EnterChannelName,
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
    }
}
