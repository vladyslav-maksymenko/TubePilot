using System.Globalization;

namespace TubePilot.Infrastructure.Telegram;

internal static class PublishingScheduleHelper
{
    private const string DefaultTimeZoneId = "Europe/Kiev";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";
    private static readonly string[] TimeOfDayFormats = ["h\\:mm", "hh\\:mm", "h\\:mm\\:ss", "hh\\:mm\\:ss"];

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
            errorMessage = "Р’РІРµРґРё РґР°С‚Сѓ С– С‡Р°СЃ Сѓ С„РѕСЂРјР°С‚С– YYYY-MM-DD HH:mm.";
            return false;
        }

        if (!DateTime.TryParseExact(
                input.Trim(),
                DateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
        {
            errorMessage = "РќРµ РјРѕР¶Сѓ РїСЂРѕС‡РёС‚Р°С‚Рё РґР°С‚Сѓ. Р¤РѕСЂРјР°С‚ РјР°С” Р±СѓС‚Рё YYYY-MM-DD HH:mm.";
            return false;
        }

        var timeZone = ResolveTimeZone(timeZoneId);
        var localUnspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        var localAsUtc = TimeZoneInfo.ConvertTimeToUtc(localUnspecified, timeZone);
        scheduledPublishAtUtc = new DateTimeOffset(localAsUtc, TimeSpan.Zero);

        if (scheduledPublishAtUtc <= utcNow.AddMinutes(2))
        {
            errorMessage = "Р”Р°С‚Р° С– С‡Р°СЃ РјР°СЋС‚СЊ Р±СѓС‚Рё РІ РјР°Р№Р±СѓС‚РЅСЊРѕРјСѓ. РЎРїСЂРѕР±СѓР№ С‰Рµ СЂР°Р·.";
            scheduledPublishAtUtc = default;
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    public static bool TryParseTimeOfDay(string input, out TimeSpan timeOfDay)
        => TimeSpan.TryParseExact(
            input?.Trim() ?? string.Empty,
            TimeOfDayFormats,
            CultureInfo.InvariantCulture,
            out timeOfDay);

    public static DateTimeOffset GetNextFreeSlotUtc(
        DateTimeOffset utcNow,
        DateTimeOffset? lastScheduledAtUtc,
        string? timeZoneId,
        string dailyPublishTime)
    {
        if (!TryParseTimeOfDay(dailyPublishTime, out var timeOfDay))
        {
            timeOfDay = new TimeSpan(10, 0, 0);
        }

        var timeZone = ResolveTimeZone(timeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(utcNow, timeZone);

        DateTime baseLocalDate;
        if (lastScheduledAtUtc is null)
        {
            baseLocalDate = nowLocal.Date;
        }
        else
        {
            var lastLocal = TimeZoneInfo.ConvertTime(lastScheduledAtUtc.Value, timeZone);
            baseLocalDate = lastLocal.Date.AddDays(1);
        }

        var candidateLocal = baseLocalDate.Add(timeOfDay);
        var candidateUtc = ConvertLocalUnspecifiedToUtc(candidateLocal, timeZone);

        if (candidateUtc <= utcNow.AddMinutes(2))
        {
            candidateLocal = candidateLocal.AddDays(1);
            candidateUtc = ConvertLocalUnspecifiedToUtc(candidateLocal, timeZone);
        }

        return new DateTimeOffset(candidateUtc, TimeSpan.Zero);
    }

    public static DateTimeOffset AddLocalDays(DateTimeOffset utcTime, int days, string? timeZoneId)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(utcTime, timeZone);
        var localNext = local.Date.AddDays(days).Add(local.TimeOfDay);
        var utcNext = ConvertLocalUnspecifiedToUtc(localNext, timeZone);
        return new DateTimeOffset(utcNext, TimeSpan.Zero);
    }

    public static string FormatPublishTime(DateTimeOffset? scheduledPublishAtUtc, string? timeZoneId)
    {
        if (scheduledPublishAtUtc is null)
        {
            return "рџљЂ Р—Р°СЂР°Р·";
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

    private static DateTime ConvertLocalUnspecifiedToUtc(DateTime localUnspecified, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }
}
