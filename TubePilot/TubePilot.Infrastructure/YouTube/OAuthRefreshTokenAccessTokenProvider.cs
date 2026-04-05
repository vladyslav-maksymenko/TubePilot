using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.YouTube.Options;

namespace TubePilot.Infrastructure.YouTube;

internal interface IYouTubeAccessTokenProvider
{
    /// <summary>
    /// Get access token using global config (secrets.json YouTube section).
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct);

    /// <summary>
    /// Get access token using per-group credentials.
    /// </summary>
    Task<string> GetAccessTokenAsync(YouTubeUploadCredentials credentials, CancellationToken ct);
}

internal sealed class OAuthRefreshTokenAccessTokenProvider(
    HttpClient httpClient,
    IOptionsMonitor<YouTubeOptions> optionsMonitor,
    ILogger<OAuthRefreshTokenAccessTokenProvider> logger) : IYouTubeAccessTokenProvider
{
    private readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var options = optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.ClientId) ||
            string.IsNullOrWhiteSpace(options.ClientSecret) ||
            string.IsNullOrWhiteSpace(options.RefreshToken))
        {
            throw new InvalidOperationException("YouTube OAuth2 config is missing. Provide YouTube:ClientId, YouTube:ClientSecret, YouTube:RefreshToken.");
        }

        return GetAccessTokenAsync(
            new YouTubeUploadCredentials(options.ClientId, options.ClientSecret, options.RefreshToken), ct);
    }

    public async Task<string> GetAccessTokenAsync(YouTubeUploadCredentials credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var cacheKey = credentials.RefreshToken;
        var nowUtc = DateTimeOffset.UtcNow;

        if (_tokenCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > nowUtc.AddMinutes(2))
        {
            return cached.AccessToken;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            nowUtc = DateTimeOffset.UtcNow;
            if (_tokenCache.TryGetValue(cacheKey, out cached) && cached.ExpiresAtUtc > nowUtc.AddMinutes(2))
            {
                return cached.AccessToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", credentials.ClientId),
                new KeyValuePair<string, string>("client_secret", credentials.ClientSecret),
                new KeyValuePair<string, string>("refresh_token", credentials.RefreshToken),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            ]);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "YouTube OAuth token refresh failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
                    (int)response.StatusCode, response.ReasonPhrase, responseBody);
                throw new HttpRequestException($"YouTube OAuth token refresh failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("access_token", out var accessTokenElement) ||
                accessTokenElement.GetString() is not { Length: > 0 } accessToken)
            {
                throw new InvalidOperationException("YouTube OAuth token refresh response is missing access_token.");
            }

            var expiresInSeconds = doc.RootElement.TryGetProperty("expires_in", out var expiresInElement)
                ? expiresInElement.GetInt32()
                : 3600;

            _tokenCache[cacheKey] = new CachedToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds));
            return accessToken;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAtUtc);
}
