using System.Net;

namespace TubePilot.Infrastructure.Telegram;

internal static class TelegramResultLinks
{
    public static string BuildPublicResultUrl(string baseUrl, string fileName)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        return $"{baseUrl.TrimEnd('/')}/play/{Uri.EscapeDataString(fileName)}";
    }

    public static string BuildResultMessage(string resultFileName, string resultFilePath, string? publicUrl)
    {
        var lines = new List<string>
        {
            "🎬 <b>ГОТОВИЙ ФАЙЛ:</b>",
            $"<code>{H(resultFileName)}</code>",
            string.Empty,
            $"📁 Локально: <code>{H(resultFilePath)}</code>"
        };

        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            lines.Add($"🌐 <a href=\"{H(publicUrl)}\">Відкрити плеєр</a>");
        }

        return string.Join('\n', lines);
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);
}
