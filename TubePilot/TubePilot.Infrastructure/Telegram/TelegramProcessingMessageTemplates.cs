using System.Net;

namespace TubePilot.Infrastructure.Telegram;

internal static class TelegramProcessingMessageTemplates
{
    public static string BuildQueuedStatusText(string fileName, int queuePosition)
        => $"⏳ <b>GPU ОБРОБКА: ЧЕРГА</b>\n\n<blockquote>👤 <code>{H(fileName)}</code></blockquote>\n\n📌 <code>Queued (#{queuePosition})</code>\n⏱ <i>Очікує вільний слот...</i>";

    public static string BuildProcessingStartText(string fileName)
        => $"⚙️ <b>GPU ОБРОБКА: АКТИВНО</b>\n\n<blockquote>👤 <code>{H(fileName)}</code></blockquote>\n\n📊 <code>[----------] 0%</code>\n🔄 <i>Ініціалізація FFmpeg Engine...</i>";

    private static string H(string text) => WebUtility.HtmlEncode(text);
}

