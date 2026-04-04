using System.Globalization;
using System.Net;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.Telegram.Models;

namespace TubePilot.Infrastructure.Telegram;

internal static class TelegramSegmentResultMessageBuilder
{
    public static string BuildResultMessage(PublishedResultContext context)
    {
        var lines = new List<string>
        {
            "📹 <b>Результат готовий</b>",
            $"<code>{H(context.ResultFileName)}</code>",
            string.Empty,
            $"⏱ <b>Тривалість</b>: <code>{FormatDuration(context.DurationSeconds)}</code>",
            $"📦 <b>Розмір</b>: <code>{FormatSize(context.SizeBytes)}</code>",
            string.Empty,
            "🛠 <b>Застосовано</b>:"
        };

        if (context.TotalParts > 1)
        {
            lines.Insert(3, $"🧩 <b>Part</b>: <code>{context.PartNumber}/{context.TotalParts}</code>");
        }

        lines.AddRange(BuildAppliedOptionLines(context.ProcessingSummary));

        if (context.ProcessingSummary.SkippedReasons.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("⏭ <b>Пропущено</b>:");
            lines.AddRange(context.ProcessingSummary.SkippedReasons.Select(r => $"• {r}"));
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<string> BuildAppliedOptionLines(VideoProcessingSummary summary)
    {
        var lines = new List<string>();

        if (summary.Slice is not null)
        {
            var slice = summary.Slice.Value;
            var start = FormatDuration(slice.StartSeconds);
            var end = FormatDuration(slice.StartSeconds + slice.DurationSeconds);
            lines.Add($"• ✂️ Слайс: <code>{start}–{end}</code> (<code>{FormatDuration(slice.DurationSeconds)}</code>)");
        }

        if (summary.Mirror)
        {
            lines.Add("• 🪞 Дзеркало (HFlip)");
        }

        if (summary.Volume is not null)
        {
            var factor = summary.Volume.Value.Factor;
            var percent = (int)Math.Round((1d - factor) * 100d);
            lines.Add(FormattableString.Invariant($"• 🔉 Гучність: <code>-{percent}%</code> (<code>x{factor:0.00}</code>)"));
        }

        if (summary.Speed is not null)
        {
            var speedFactor = summary.Speed.Value.SpeedFactor;
            var emoji = speedFactor >= 1d ? "⚡" : "🐌";
            lines.Add(FormattableString.Invariant($"• {emoji} Швидкість: <code>x{speedFactor:0.00}</code>"));
        }

        if (summary.ColorCorrection is not null)
        {
            var color = summary.ColorCorrection.Value;
            lines.Add(FormattableString.Invariant(
                $"• 🎨 Колір: <code>sat {color.Saturation:0.000}</code>, <code>bri {color.Brightness:0.000}</code>, <code>gamma {color.Gamma:0.000}</code>"));
        }

        if (summary.QrOverlay)
        {
            lines.Add("• 📱 QR overlay: <code>0–10s</code>");
        }

        if (summary.Rotate is not null)
        {
            var rotation = summary.Rotate.Value;
            lines.Add(FormattableString.Invariant($"• 🔄 Поворот: <code>{rotation.Degrees:0.0}°</code> (<code>zoom x{rotation.Zoom:0.00}</code>)"));
        }

        if (summary.Downscale is not null)
        {
            lines.Add("• 📐 Даунскейл: <code>1080p</code>");
        }

        if (lines.Count == 0)
        {
            lines.Add("• —");
        }

        return lines;
    }

    private static string FormatDuration(double seconds)
    {
        var roundedSeconds = Math.Max(0, (int)Math.Round(seconds, MidpointRounding.AwayFromZero));
        var ts = TimeSpan.FromSeconds(roundedSeconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatSize(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        if (bytes >= gb)
        {
            return FormattableString.Invariant($"{bytes / gb:0.00} GB");
        }

        if (bytes >= mb)
        {
            return FormattableString.Invariant($"{bytes / mb:0.0} MB");
        }

        if (bytes >= kb)
        {
            return FormattableString.Invariant($"{bytes / kb:0.0} KB");
        }

        return FormattableString.Invariant($"{bytes} B");
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);
}

