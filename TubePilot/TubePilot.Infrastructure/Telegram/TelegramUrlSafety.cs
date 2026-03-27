using System.Net;

namespace TubePilot.Infrastructure.Telegram;

internal static class TelegramUrlSafety
{
    public static bool IsTelegramSafeButtonUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
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

