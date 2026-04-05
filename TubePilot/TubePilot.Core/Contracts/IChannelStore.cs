using TubePilot.Core.Domain;

namespace TubePilot.Core.Contracts;

public interface IChannelStore
{
    IReadOnlyList<GmailGroup> GetAllGroups();
    GmailGroup? GetGroup(string groupId);
    GmailGroup? GetGroupForChannel(string channelId);
    YouTubeChannel? GetChannel(string channelId);
    IReadOnlyList<YouTubeChannel> GetAllChannelsWithFolders();

    void AddGroup(GmailGroup group);
    void UpdateGroup(GmailGroup group);
    void RemoveGroup(string groupId);

    void AddChannel(string groupId, YouTubeChannel channel);
    void UpdateChannel(string groupId, YouTubeChannel channel);
    void RemoveChannel(string groupId, string channelId);

    void RecordQuotaUsage(string groupId, double units);
    void ResetQuotaIfNeeded(string groupId);
    double GetRemainingQuota(string groupId);
}
