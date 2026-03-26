using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Infrastructure.YouTube.Options;

namespace TubePilot.Infrastructure.YouTube;

internal interface IYouTubeAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}

internal sealed class OAuthRefreshTokenAccessTokenProvider(
    HttpClient httpClient,
    IOptionsMonitor<YouTubeOptions> optionsMonitor,
    ILogger<OAuthRefreshTokenAccessTokenProvider> logger) : IYouTubeAccessTokenProvider
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAtUtc;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (_accessToken is not null && _accessTokenExpiresAtUtc > nowUtc.AddMinutes(2))
        {
            return _accessToken;
        }

        await _mutex.WaitAsync(ct);
        try
        {
            nowUtc = DateTimeOffset.UtcNow;
            if (_accessToken is not null && _accessTokenExpiresAtUtc > nowUtc.AddMinutes(2))
            {
                return _accessToken;
            }

            var options = optionsMonitor.CurrentValue;
            if (string.IsNullOrWhiteSpace(options.ClientId) ||
                string.IsNullOrWhiteSpace(options.ClientSecret) ||
                string.IsNullOrWhiteSpace(options.RefreshToken))
            {
                throw new InvalidOperationException("YouTube OAuth2 config is missing. Provide YouTube:ClientId, YouTube:ClientSecret, YouTube:RefreshToken.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", options.ClientId),
                new KeyValuePair<string, string>("client_secret", options.ClientSecret),
                new KeyValuePair<string, string>("refresh_token", options.RefreshToken),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            ]);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "YouTube OAuth token refresh failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    responseBody);

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

            _accessToken = accessToken;
            _accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);
            return accessToken;
        }
        finally
        {
            _mutex.Release();
        }
    }
}
