using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types.Enums;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Telegram.Models;
using TubePilot.Infrastructure.Telegram.Options;
using TubePilot.Infrastructure.YouTube.Options;

namespace TubePilot.Infrastructure.Telegram;

internal sealed class TelegramUploadJobRunner(
    ITelegramUiClient ui,
    IYouTubeUploader youTubeUploader,
    IGoogleSheetsLogger googleSheetsLogger,
    ITelegramResultThumbnailGenerator thumbnailGenerator,
    IChannelStore channelStore,
    IOptionsMonitor<PublishingOptions> publishingOptions,
    IOptionsMonitor<YouTubeOptions> youTubeOptions,
    ILogger<TelegramUploadJobRunner> logger)
{
    public async Task RunAsync(
        PublishWizardSession session,
        Action<PublishWizardSession, YouTubeUploadResult> recordLastScheduledAt,
        Func<PublishWizardSession, DateTimeOffset?> resolveNextSlot,
        CancellationToken ct)
    {
        try
        {
            if (session.IsBulkPublish)
                await RunBulkAsync(session, recordLastScheduledAt, resolveNextSlot, ct);
            else
                await RunSingleAsync(session, recordLastScheduledAt, ct);
        }
        catch (OperationCanceledException)
        {
            await SendCancelledAsync(session, ct);
        }
        catch (Exception ex)
        {
            var ctx = session.IsBulkPublish
                ? session.ResultContexts[Math.Clamp(session.CurrentBulkIndex, 0, session.ResultContexts.Count - 1)]
                : session.ResultContext;
            logger.LogError(ex, "YouTube upload failed for {FileName}.", ctx.ResultFileName);
            await SendFailureAsync(session, ex, ct);
        }
    }

    private async Task RunSingleAsync(
        PublishWizardSession session,
        Action<PublishWizardSession, YouTubeUploadResult> recordLastScheduledAt,
        CancellationToken ct)
    {
        string? thumbPath = null;
        try
        {
            thumbPath = await thumbnailGenerator.TryGenerateAsync(session.ResultContext.ResultFilePath, ct);
            var request = BuildRequest(session, session.ResultContext.ResultFilePath, session.Title, session.ScheduledPublishAtUtc, thumbPath);

            var result = await youTubeUploader.UploadAsync(request, pct => UpdateProgressAsync(session, pct, ct), ct);

            await googleSheetsLogger.LogUploadAsync(
                NormalizeChannel(session.ChannelName), session.ResultContext.SourceFileName, session.Title,
                result.VideoId, result.YouTubeUrl, result.Status.ToString().ToLowerInvariant(), result.ScheduledAtUtc, ct);

            recordLastScheduledAt(session, result);
            RecordQuota(session);
            await SendSuccessAsync(session, result, ct);
        }
        finally { TryDeleteQuietly(thumbPath); }
    }

    private async Task RunBulkAsync(
        PublishWizardSession session,
        Action<PublishWizardSession, YouTubeUploadResult> recordLastScheduledAt,
        Func<PublishWizardSession, DateTimeOffset?> resolveNextSlot,
        CancellationToken ct)
    {
        var tzId = publishingOptions.CurrentValue.TimeZoneId;
        var baseScheduledAtUtc = session.ScheduledPublishAtUtc;
        var results = new List<(PublishedResultContext Ctx, string Title, YouTubeUploadResult Result)>(session.ResultContexts.Count);

        for (var i = 0; i < session.ResultContexts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            session.CurrentBulkIndex = i;
            session.LastProgressPercent = -1;

            var context = session.ResultContexts[i];
            var title = session.TitleTemplate.Replace("{N}", context.PartNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            DateTimeOffset? scheduledAtUtc = baseScheduledAtUtc is null
                ? (i == 0 ? null : resolveNextSlot(session))
                : PublishingScheduleHelper.AddLocalDays(baseScheduledAtUtc.Value, i, tzId);

            session.Title = title;
            session.ScheduledPublishAtUtc = scheduledAtUtc;

            string? thumbPath = null;
            try
            {
                thumbPath = await thumbnailGenerator.TryGenerateAsync(context.ResultFilePath, ct);
                var request = BuildRequest(session, context.ResultFilePath, title, scheduledAtUtc, thumbPath);
                var result = await youTubeUploader.UploadAsync(request, pct => UpdateProgressAsync(session, pct, ct), ct);

                await googleSheetsLogger.LogUploadAsync(
                    NormalizeChannel(session.ChannelName), context.SourceFileName, title,
                    result.VideoId, result.YouTubeUrl, result.Status.ToString().ToLowerInvariant(), result.ScheduledAtUtc, ct);

                recordLastScheduledAt(session, result);
                RecordQuota(session);
                results.Add((context, title, result));
            }
            finally { TryDeleteQuietly(thumbPath); }
        }

        await SendBulkSuccessAsync(session, results, ct);
    }

    private YouTubeUploadRequest BuildRequest(PublishWizardSession session, string filePath, string title, DateTimeOffset? scheduledAtUtc, string? thumbPath)
    {
        YouTubeUploadCredentials? credentials = null;
        if (session.StoreGroupId is not null && session.StoreChannelId is not null)
        {
            var group = channelStore.GetGroup(session.StoreGroupId);
            var channel = group?.Channels.FirstOrDefault(c => c.Id == session.StoreChannelId);
            if (group is not null && channel is not null && !string.IsNullOrWhiteSpace(channel.RefreshToken))
            {
                credentials = new YouTubeUploadCredentials(group.ClientId, group.ClientSecret, channel.RefreshToken);
            }
        }

        return new YouTubeUploadRequest(filePath, title, session.Description, session.Tags,
            Visibility: session.Visibility, ScheduledPublishAtUtc: scheduledAtUtc,
            ThumbnailFilePath: thumbPath, CategoryId: youTubeOptions.CurrentValue.DefaultCategoryId,
            Credentials: credentials);
    }

    internal async Task UpdateProgressAsync(PublishWizardSession session, int percent, CancellationToken ct)
    {
        if (session.ProgressMessageId is null || percent <= session.LastProgressPercent) return;
        session.LastProgressPercent = percent;
        await ui.EditMessageTextAsync(session.ChatId, session.ProgressMessageId.Value, BuildProgressText(session, percent), parseMode: ParseMode.Html, ct: ct);
    }

    internal static string BuildProgressText(PublishWizardSession session, int percent)
    {
        var label = session.ScheduledPublishAtUtc is null ? "Published" : "Scheduled";
        var context = session.IsBulkPublish
            ? session.ResultContexts[Math.Clamp(session.CurrentBulkIndex, 0, session.ResultContexts.Count - 1)]
            : session.ResultContext;
        var bulkLine = session.IsBulkPublish ? $"📦 Part {context.PartNumber}/{session.ResultContexts.Count}\n\n" : string.Empty;

        return $"📤 <b>{label} upload to YouTube...</b>\n\n{bulkLine}" +
               $"<blockquote>📁 <code>{H(context.ResultFileName)}</code>\n📝 <code>{H(session.Title)}</code></blockquote>\n\n" +
               $"📊 <code>[{new string('#', percent / 10)}{new string('-', 10 - percent / 10)}] {percent}%</code>";
    }

    private async Task SendSuccessAsync(PublishWizardSession session, YouTubeUploadResult result, CancellationToken ct)
    {
        var text = $"✅ <b>{(result.Status == YouTubeUploadStatus.Scheduled ? "Scheduled" : "Published")}</b>\n\n" +
                   $"<blockquote>📁 <code>{H(session.ResultContext.ResultFileName)}</code>\n📝 <code>{H(session.Title)}</code>\n" +
                   $"🔗 <a href=\"{H(result.YouTubeUrl)}\">Open on YouTube</a></blockquote>";
        await EditOrSendAsync(session, text, ct);
    }

    private async Task SendBulkSuccessAsync(PublishWizardSession session,
        IReadOnlyList<(PublishedResultContext Ctx, string Title, YouTubeUploadResult Result)> results, CancellationToken ct)
    {
        var channelName = string.IsNullOrWhiteSpace(session.ChannelName) ? "Default" : session.ChannelName;
        var visible = results.Take(10).ToArray();
        var lines = visible.Select(r => $"• Part {r.Ctx.PartNumber}: <a href=\"{H(r.Result.YouTubeUrl)}\">{H(r.Title)}</a> ({H(r.Result.Status.ToString())})");
        var suffix = results.Count > visible.Length ? $"\n… +{results.Count - visible.Length} more" : string.Empty;

        var text = "✅ <b>Bulk upload completed</b>\n\n" +
                   $"<blockquote>📺 <code>{H(channelName)}</code>\n📁 <code>{H(session.ResultContext.SourceFileName)}</code>\n" +
                   $"📦 Segments: <b>{results.Count}</b></blockquote>\n\n" + string.Join('\n', lines) + suffix;
        await EditOrSendAsync(session, text, ct);
    }

    internal async Task SendFailureAsync(PublishWizardSession session, Exception ex, CancellationToken ct)
    {
        var text = $"❌ <b>Upload failed</b>\n\n<pre>{H(ex.Message)}</pre>";
        await EditOrSendAsync(session, text, ct);
    }

    internal async Task SendCancelledAsync(PublishWizardSession session, CancellationToken ct)
    {
        await EditOrSendAsync(session, "❌ <b>Upload cancelled</b>", ct);
    }

    private async Task EditOrSendAsync(PublishWizardSession session, string text, CancellationToken ct)
    {
        if (session.ProgressMessageId is not null)
            await ui.EditMessageTextAsync(session.ChatId, session.ProgressMessageId.Value, text, parseMode: ParseMode.Html, ct: ct);
        else
            await ui.SendMessageAsync(session.ChatId, text, parseMode: ParseMode.Html, replyToMessageId: session.ReplyToMessageId, ct: ct);
    }

    private static string NormalizeChannel(string? name) => string.IsNullOrWhiteSpace(name) ? "Default" : name.Trim();

    private void RecordQuota(PublishWizardSession session)
    {
        if (session.StoreGroupId is not null)
            channelStore.RecordQuotaUsage(session.StoreGroupId, 1650);
    }

    private static void TryDeleteQuietly(string? path) { if (string.IsNullOrWhiteSpace(path)) return; try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static string H(string text) => WebUtility.HtmlEncode(text);
}
