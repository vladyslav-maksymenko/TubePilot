using System.Globalization;

namespace TubePilot.Infrastructure.Telegram;

internal static class PublishingScheduleHelper
{
    private const string DefaultTimeZoneId = "Europe/Kiev";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    public static string GetDefaultTitle(string fileName)
        => Path.GetFileNameWithoutExtension(fileName);

    public static string NormalizeTags(string? rawTags)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
        {
            return string.Empty;
        }

        var tags = rawTags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join(", ", tags);
    }

    public static IReadOnlyList<string> ParseTags(string? rawTags)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
        {
            return [];
        }

        return rawTags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool TryParseScheduledPublishAt(
        string input,
        string? timeZoneId,
        DateTimeOffset utcNow,
        out DateTimeOffset scheduledPublishAtUtc,
        out string errorMessage)
    {
        scheduledPublishAtUtc = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "Введи дату і час у форматі YYYY-MM-DD HH:mm.";
            return false;
        }

        if (!DateTime.TryParseExact(
                input.Trim(),
                DateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
        {
            errorMessage = "Не можу прочитати дату. Формат має бути YYYY-MM-DD HH:mm.";
            return false;
        }

        var timeZone = ResolveTimeZone(timeZoneId);
        var localUnspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        var localAsUtc = TimeZoneInfo.ConvertTimeToUtc(localUnspecified, timeZone);
        scheduledPublishAtUtc = new DateTimeOffset(localAsUtc, TimeSpan.Zero);

        if (scheduledPublishAtUtc <= utcNow.AddMinutes(2))
        {
            errorMessage = "Дата і час мають бути в майбутньому. Спробуй ще раз.";
            scheduledPublishAtUtc = default;
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    public static string FormatPublishTime(DateTimeOffset? scheduledPublishAtUtc, string? timeZoneId)
    {
        if (scheduledPublishAtUtc is null)
        {
            return "🚀 Зараз";
        }

        var timeZone = ResolveTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(scheduledPublishAtUtc.Value, timeZone);
        return $"{local:yyyy-MM-dd HH:mm} ({timeZone.Id})";
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var candidate = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim();

        if (TryFindTimeZone(candidate, out var timeZone))
        {
            return timeZone;
        }

        if (candidate.Equals("Europe/Kiev", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("Europe/Kyiv", StringComparison.OrdinalIgnoreCase))
        {
            if (TryFindTimeZone("FLE Standard Time", out timeZone))
            {
                return timeZone;
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
