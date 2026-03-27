using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TubePilot.Infrastructure.YouTube;

internal sealed record YouTubeChannelInfo(string Id, string Title);

internal interface IYouTubeChannelLookup
{
    Task<IReadOnlyList<YouTubeChannelInfo>> GetChannelsAsync(CancellationToken ct);
}

internal sealed class YouTubeChannelLookup(
    HttpClient httpClient,
    IYouTubeAccessTokenProvider accessTokenProvider,
    ILogger<YouTubeChannelLookup> logger) : IYouTubeChannelLookup
{
    private static readonly Uri ChannelsEndpoint = new("https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true&maxResults=50");

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private IReadOnlyList<YouTubeChannelInfo> _cachedChannels = Array.Empty<YouTubeChannelInfo>();
    private DateTimeOffset _cacheExpiresAtUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<YouTubeChannelInfo>> GetChannelsAsync(CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc < _cacheExpiresAtUtc && _cachedChannels.Count > 0)
        {
            return _cachedChannels;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc < _cacheExpiresAtUtc && _cachedChannels.Count > 0)
            {
                return _cachedChannels;
            }

            try
            {
                var token = await accessTokenProvider.GetAccessTokenAsync(ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, ChannelsEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "YouTube channels.list failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        responseBody.Length > 300 ? responseBody[..300] : responseBody);

                    _cachedChannels = Array.Empty<YouTubeChannelInfo>();
                    _cacheExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
                    return _cachedChannels;
                }

                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    _cachedChannels = Array.Empty<YouTubeChannelInfo>();
                    _cacheExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
                    return _cachedChannels;
                }

                var channels = new List<YouTubeChannelInfo>();
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    var title = item.TryGetProperty("snippet", out var snippet) &&
                                snippet.TryGetProperty("title", out var titleElement)
                        ? titleElement.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    channels.Add(new YouTubeChannelInfo(id, title));
                }

                _cachedChannels = channels;
                _cacheExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10);
                return _cachedChannels;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to fetch YouTube channels via API.");
                _cachedChannels = Array.Empty<YouTubeChannelInfo>();
                _cacheExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
                return _cachedChannels;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }
}

