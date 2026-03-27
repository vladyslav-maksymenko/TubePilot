using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace TubePilot.Worker.Tests;

public sealed class ProcessedVideoEndpointsTests
{
    [Fact]
    public async Task PlayEndpoint_ReturnsHtmlPlayerPage()
    {
        using var fixture = await CreateFixtureAsync(("sample video.mp4", [0, 1, 2, 3]));

        var response = await fixture.Client.GetAsync("/play/sample%20video.mp4");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("<video controls", body, StringComparison.Ordinal);
        Assert.Contains("/video/sample%20video.mp4", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VideoEndpoint_SupportsRangeRequests()
    {
        using var fixture = await CreateFixtureAsync(("range.mp4", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/video/range.mp4");
        request.Headers.Range = new RangeHeaderValue(2, 5);

        var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(2, response.Content.Headers.ContentRange?.From);
        Assert.Equal(5, response.Content.Headers.ContentRange?.To);
        Assert.Equal(10, response.Content.Headers.ContentRange?.Length);
        Assert.Equal([2, 3, 4, 5], body);
    }

    [Fact]
    public async Task PlayEndpoint_ForMissingFile_ReturnsNotFound()
    {
        using var fixture = await CreateFixtureAsync();

        var response = await fixture.Client.GetAsync("/play/missing.mp4");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task VideoEndpoint_ForMissingFile_ReturnsNotFound()
    {
        using var fixture = await CreateFixtureAsync();

        var response = await fixture.Client.GetAsync("/video/missing.mp4");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task VideoEndpoint_BlocksDirectoryTraversal()
    {
        using var fixture = await CreateFixtureAsync(("safe.mp4", [1, 2, 3]));

        var response = await fixture.Client.GetAsync("/video/..");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<TestFixture> CreateFixtureAsync(params (string FileName, byte[] Content)[] files)
    {
        var processedDirectory = Path.Combine(Path.GetTempPath(), $"tubepilot-worker-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(processedDirectory);

        foreach (var (fileName, content) in files)
        {
            await File.WriteAllBytesAsync(Path.Combine(processedDirectory, fileName), content);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        ProcessedVideoEndpoints.MapRoutes(app, processedDirectory);
        await app.StartAsync();

        return new TestFixture(processedDirectory, app, app.GetTestClient());
    }

    private sealed class TestFixture(string processedDirectory, WebApplication app, HttpClient client) : IDisposable
    {
        public string ProcessedDirectory { get; } = processedDirectory;

        public WebApplication App { get; } = app;

        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            App.DisposeAsync().AsTask().GetAwaiter().GetResult();

            if (Directory.Exists(ProcessedDirectory))
            {
                Directory.Delete(ProcessedDirectory, recursive: true);
            }
        }
    }
}
