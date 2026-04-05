using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TubePilot.Core.Contracts;
using TubePilot.Core.Domain;

namespace TubePilot.Infrastructure.Channels;

internal sealed class JsonChannelStore : IChannelStore
{
    private const string FileName = "channels.json";
    private const double DailyQuotaLimit = 10_000;
    private const double UploadCost = 1_650; // 1600 upload + 50 thumbnail

    private static readonly TimeZoneInfo PacificTimeZone = ResolvePacificTimeZone();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _lock = new();
    private readonly ILogger<JsonChannelStore> _logger;
    private List<GmailGroup>? _cached;

    public JsonChannelStore(ILogger<JsonChannelStore> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<GmailGroup> GetAllGroups()
    {
        lock (_lock) return Load().ToList();
    }

    public GmailGroup? GetGroup(string groupId)
    {
        lock (_lock) return Load().FirstOrDefault(g => g.Id == groupId);
    }

    public GmailGroup? GetGroupForChannel(string channelId)
    {
        lock (_lock) return Load().FirstOrDefault(g => g.Channels.Any(c => c.Id == channelId));
    }

    public YouTubeChannel? GetChannel(string channelId)
    {
        lock (_lock) return Load().SelectMany(g => g.Channels).FirstOrDefault(c => c.Id == channelId);
    }

    public IReadOnlyList<YouTubeChannel> GetAllChannelsWithFolders()
    {
        lock (_lock)
            return Load()
                .SelectMany(g => g.Channels)
                .Where(c => !string.IsNullOrWhiteSpace(c.DriveFolderId))
                .ToList();
    }

    public void AddGroup(GmailGroup group)
    {
        lock (_lock)
        {
            var groups = Load();
            groups.Add(group);
            Save(groups);
        }
    }

    public void UpdateGroup(GmailGroup group)
    {
        lock (_lock)
        {
            var groups = Load();
            var idx = groups.FindIndex(g => g.Id == group.Id);
            if (idx >= 0)
            {
                groups[idx] = group;
                Save(groups);
            }
        }
    }

    public void RemoveGroup(string groupId)
    {
        lock (_lock)
        {
            var groups = Load();
            groups.RemoveAll(g => g.Id == groupId);
            Save(groups);
        }
    }

    public void AddChannel(string groupId, YouTubeChannel channel)
    {
        lock (_lock)
        {
            var groups = Load();
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            group.Channels.Add(channel);
            Save(groups);
        }
    }

    public void UpdateChannel(string groupId, YouTubeChannel channel)
    {
        lock (_lock)
        {
            var groups = Load();
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            var idx = group.Channels.FindIndex(c => c.Id == channel.Id);
            if (idx >= 0)
            {
                group.Channels[idx] = channel;
                Save(groups);
            }
        }
    }

    public void RemoveChannel(string groupId, string channelId)
    {
        lock (_lock)
        {
            var groups = Load();
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            group.Channels.RemoveAll(c => c.Id == channelId);
            Save(groups);
        }
    }

    public void RecordQuotaUsage(string groupId, double units)
    {
        lock (_lock)
        {
            var groups = Load();
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            ResetQuotaIfNeededInternal(group);
            group.QuotaUsedToday += units;
            Save(groups);
        }
    }

    public void ResetQuotaIfNeeded(string groupId)
    {
        lock (_lock)
        {
            var groups = Load();
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            if (ResetQuotaIfNeededInternal(group))
                Save(groups);
        }
    }

    public double GetRemainingQuota(string groupId)
    {
        lock (_lock)
        {
            var groups = Load();
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return 0;
            ResetQuotaIfNeededInternal(group);
            return Math.Max(0, DailyQuotaLimit - group.QuotaUsedToday);
        }
    }

    private static bool ResetQuotaIfNeededInternal(GmailGroup group)
    {
        var now = DateTimeOffset.UtcNow;
        if (now >= group.QuotaResetAtUtc)
        {
            group.QuotaUsedToday = 0;
            group.QuotaResetAtUtc = GetNextPacificMidnightUtc(now);
            return true;
        }
        return false;
    }

    private static DateTimeOffset GetNextPacificMidnightUtc(DateTimeOffset utcNow)
    {
        var pacificNow = TimeZoneInfo.ConvertTime(utcNow, PacificTimeZone);
        var nextMidnightLocal = pacificNow.Date.AddDays(1);
        var nextMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(nextMidnightLocal, DateTimeKind.Unspecified), PacificTimeZone);
        return new DateTimeOffset(nextMidnightUtc, TimeSpan.Zero);
    }

    private List<GmailGroup> Load()
    {
        if (_cached is not null) return _cached;

        if (!File.Exists(FileName))
        {
            _cached = [];
            return _cached;
        }

        try
        {
            var json = File.ReadAllText(FileName);
            _cached = JsonSerializer.Deserialize<List<GmailGroup>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {FileName}, starting fresh.", FileName);
            _cached = [];
        }

        return _cached;
    }

    private void Save(List<GmailGroup> groups)
    {
        _cached = groups;
        try
        {
            var json = JsonSerializer.Serialize(groups, JsonOptions);
            var tempPath = FileName + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save {FileName}.", FileName);
        }
    }

    private static TimeZoneInfo ResolvePacificTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (TimeZoneNotFoundException) { }
        try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.Utc;
    }
}
