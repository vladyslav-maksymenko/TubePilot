using System.Globalization;

namespace TubePilot.Infrastructure.Video;

internal static class FfmpegProgressParser
{
    internal static int? TryParsePercent(string line, double durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return null;
        }

        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
            !line.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            return null;
        }

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0 || equalsIndex == line.Length - 1)
        {
            return null;
        }

        if (!long.TryParse(line[(equalsIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawValue))
        {
            return null;
        }

        var seconds = rawValue / 1_000_000d;
        var progress = (int)Math.Floor(seconds / durationSeconds * 100d);
        return Math.Clamp(progress, 0, 99);
    }
}
