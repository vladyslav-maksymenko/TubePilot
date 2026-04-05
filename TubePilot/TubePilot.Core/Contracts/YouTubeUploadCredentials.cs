namespace TubePilot.Core.Contracts;

/// <summary>
/// Per-group OAuth2 credentials for YouTube upload.
/// When null, falls back to global YouTube config from appsettings/secrets.
/// </summary>
public sealed record YouTubeUploadCredentials(
    string ClientId,
    string ClientSecret,
    string RefreshToken);
