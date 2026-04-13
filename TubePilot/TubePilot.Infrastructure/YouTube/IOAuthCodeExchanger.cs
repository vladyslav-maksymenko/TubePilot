namespace TubePilot.Infrastructure.YouTube;

/// <summary>
/// Exchanges a Google OAuth2 authorization code for a refresh token.
/// </summary>
internal interface IOAuthCodeExchanger
{
    Task<string> ExchangeCodeAsync(string code, string clientId, string clientSecret, string redirectUri, CancellationToken ct);
}
