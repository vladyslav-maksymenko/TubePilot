namespace TubePilot.Infrastructure.YouTube;

internal interface IYouTubeChannelLookup
{
    Task<IReadOnlyList<YouTubeChannelInfo>> GetChannelsAsync(CancellationToken ct);
    Task<IReadOnlyList<YouTubeChannelInfo>> GetChannelsAsync(string accessToken, CancellationToken ct);
}
