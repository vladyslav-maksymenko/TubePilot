using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TubePilot.Infrastructure.YouTube;

internal sealed class OAuthCodeExchanger(
    HttpClient httpClient,
    ILogger<OAuthCodeExchanger> logger) : IOAuthCodeExchanger
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    public async Task<OAuthTokenResult> ExchangeCodeAsync(
        string code, string clientId, string clientSecret, string redirectUri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("redirect_uri", redirectUri),
            new KeyValuePair<string, string>("grant_type", "authorization_code")
        ]);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("OAuth code exchange failed: {StatusCode} {Body}",
                (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
            throw new InvalidOperationException($"Google OAuth error: {response.StatusCode}. {ExtractError(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            logger.LogWarning("OAuth response missing refresh_token. Body: {Body}", body.Length > 500 ? body[..500] : body);
            throw new InvalidOperationException(
                "Google не повернув refresh_token. Переконайся що використовуєш access_type=offline і prompt=consent.");
        }

        logger.LogInformation("OAuth code exchanged successfully, refresh token obtained.");
        return new OAuthTokenResult(refreshToken, accessToken ?? "");
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error_description", out var desc))
                return desc.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString() ?? "";
        }
        catch { /* ignore parse errors */ }
        return "";
    }
}
