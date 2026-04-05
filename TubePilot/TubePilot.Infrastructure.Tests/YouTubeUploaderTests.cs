using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.YouTube;
using TubePilot.Infrastructure.YouTube.Options;

namespace TubePilot.Infrastructure.Tests;

public sealed class OAuthRefreshTokenAccessTokenProviderTests
{
    [Fact]
    public async Task GetAccessTokenAsync_RefreshesAndCachesToken()
    {
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.ToString());

            var body = await request.Content!.ReadAsStringAsync();
            var form = FormEncoding.ParseFormUrlEncoded(body);

            Assert.Equal("client-id", form["client_id"]);
            Assert.Equal("client-secret", form["client_secret"]);
            Assert.Equal("refresh-token", form["refresh_token"]);
            Assert.Equal("refresh_token", form["grant_type"]);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"ya29.test","expires_in":3600}""")
            };
        });

        var httpClient = new HttpClient(handler);
        var optionsMonitor = new StaticOptionsMonitor<YouTubeOptions>(new YouTubeOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            RefreshToken = "refresh-token"
        });

        var provider = new OAuthRefreshTokenAccessTokenProvider(
            httpClient,
            optionsMonitor,
            NullLogger<OAuthRefreshTokenAccessTokenProvider>.Instance);

        var token1 = await provider.GetAccessTokenAsync(CancellationToken.None);
        var token2 = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("ya29.test", token1);
        Assert.Equal("ya29.test", token2);
        Assert.Equal(1, handler.CallCount);
    }
}

public sealed class YouTubeUploaderRequestBuildingTests
{
    [Fact]
    public async Task UploadAsync_WhenScheduled_SetsPrivateAndPublishAt()
    {
        string? initiateJson = null;

        var handler = new RecordingHandler(async request =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.ToString().Contains("/upload/youtube/v3/videos", StringComparison.Ordinal))
            {
                initiateJson = await request.Content!.ReadAsStringAsync();

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri("https://upload.test/session");
                return response;
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.ToString() == "https://upload.test/session")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"video123"}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"unexpected request"}""")
            };
        });

        var httpClient = new HttpClient(handler);
        var tokenProvider = new StubAccessTokenProvider("access-token");
        var optionsMonitor = new StaticOptionsMonitor<YouTubeOptions>(new YouTubeOptions { DefaultCategoryId = "22" });

        var uploader = new YouTubeUploader(
            httpClient,
            tokenProvider,
            optionsMonitor,
            NullLogger<YouTubeUploader>.Instance);

        var scheduledAt = new DateTimeOffset(2026, 04, 01, 10, 00, 00, TimeSpan.Zero);
        var videoPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(videoPath, new byte[16]);

        try
        {
            var result = await uploader.UploadAsync(
                new YouTubeUploadRequest(
                    videoPath,
                    "t",
                    "d",
                    ScheduledPublishAtUtc: scheduledAt),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert.Equal("video123", result.VideoId);
        }
        finally
        {
            File.Delete(videoPath);
        }

        Assert.NotNull(initiateJson);

        using var doc = JsonDocument.Parse(initiateJson!);
        Assert.Equal("private", doc.RootElement.GetProperty("status").GetProperty("privacyStatus").GetString());

        var publishAtText = doc.RootElement.GetProperty("status").GetProperty("publishAt").GetString();
        Assert.Equal("2026-04-01T10:00:00Z", publishAtText);
    }

    [Fact]
    public async Task UploadAsync_WhenUnlisted_SetsUnlistedPrivacy()
    {
        string? initiateJson = null;

        var handler = new RecordingHandler(async request =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.ToString().Contains("/upload/youtube/v3/videos", StringComparison.Ordinal))
            {
                initiateJson = await request.Content!.ReadAsStringAsync();

                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri("https://upload.test/session");
                return response;
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.ToString() == "https://upload.test/session")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"video123"}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"unexpected request"}""")
            };
        });

        var httpClient = new HttpClient(handler);
        var tokenProvider = new StubAccessTokenProvider("access-token");
        var optionsMonitor = new StaticOptionsMonitor<YouTubeOptions>(new YouTubeOptions { DefaultCategoryId = "22" });

        var uploader = new YouTubeUploader(
            httpClient,
            tokenProvider,
            optionsMonitor,
            NullLogger<YouTubeUploader>.Instance);

        var videoPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(videoPath, new byte[16]);

        try
        {
            await uploader.UploadAsync(
                new YouTubeUploadRequest(
                    videoPath,
                    "t",
                    "d",
                    Visibility: YouTubeVideoVisibility.Unlisted),
                _ => Task.CompletedTask,
                CancellationToken.None);
        }
        finally
        {
            File.Delete(videoPath);
        }

        Assert.NotNull(initiateJson);

        using var doc = JsonDocument.Parse(initiateJson!);
        Assert.Equal("unlisted", doc.RootElement.GetProperty("status").GetProperty("privacyStatus").GetString());
        Assert.False(doc.RootElement.GetProperty("status").TryGetProperty("publishAt", out _));
    }
}

file sealed class StubAccessTokenProvider(string token) : IYouTubeAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult(token);
    public Task<string> GetAccessTokenAsync(YouTubeUploadCredentials credentials, CancellationToken ct) => Task.FromResult(token);
}

file sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    where T : class
{
    public T CurrentValue { get; } = currentValue;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

file sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return await responder(request);
    }
}

file static class FormEncoding
{
    public static Dictionary<string, string> ParseFormUrlEncoded(string form)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = form.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var idx = part.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(part[..idx].Replace('+', ' '));
            var value = Uri.UnescapeDataString(part[(idx + 1)..].Replace('+', ' '));
            dict[name] = value;
        }

        return dict;
    }
}
