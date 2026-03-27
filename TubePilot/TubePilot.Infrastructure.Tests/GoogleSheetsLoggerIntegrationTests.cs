using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TubePilot.Infrastructure.Drive.Options;
using TubePilot.Infrastructure.GoogleSheets;
using TubePilot.Infrastructure.GoogleSheets.Options;

namespace TubePilot.Infrastructure.Tests;

public sealed class GoogleSheetsLoggerIntegrationTests
{
    [Fact]
    public async Task LogUploadAsync_WhenSheetMissing_CreatesNormalizesAndAppends_ThenCachesState()
    {
        var calls = new List<RecordedCall>();
        var getSpreadsheetCount = 0;

        var handler = new RecordingHandler(async request =>
        {
            var body = await ReadBodyAsync(request.Content);
            calls.Add(new RecordedCall(request.Method, request.RequestUri!.ToString(), body));

            var url = request.RequestUri!.ToString();

            if (request.Method == HttpMethod.Get && IsSpreadsheetGet(url))
            {
                getSpreadsheetCount++;
                if (getSpreadsheetCount == 1)
                {
                    return Json("""{"sheets":[]}""");
                }

                return Json("""
                {
                  "sheets": [
                    {
                      "properties": { "title": "Audit", "sheetId": 42, "gridProperties": { "columnCount": 7 } },
                      "conditionalFormats": []
                    }
                  ]
                }
                """);
            }

            if (request.Method == HttpMethod.Get && url.Contains("/values/", StringComparison.Ordinal))
            {
                return Json("""
                {
                  "range": "Audit!A1:J1",
                  "majorDimension": "ROWS",
                  "values": [
                    ["2026-03-27T00:00:00Z","source.mp4","My title","abc123","https://youtube.test/watch?v=abc123","published","2026-03-28T10:00:00Z"]
                  ]
                }
                """);
            }

            if (request.Method == HttpMethod.Post && url.Contains(":batchUpdate", StringComparison.Ordinal))
            {
                return Json("""{"spreadsheetId":"spreadsheet123","replies":[]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"unexpected request"}""")
            };
        });

        var service = CreateSheetsService(handler);
        var sheetsOptions = new StaticOptionsMonitor<GoogleSheetsOptions>(new GoogleSheetsOptions
        {
            SpreadsheetId = "spreadsheet123",
            SheetName = "Audit"
        });
        var driveOptions = new StaticOptionsMonitor<DriveOptions>(new DriveOptions());

        var logger = new GoogleSheetsLogger(
            sheetsOptions,
            driveOptions,
            NullLogger<GoogleSheetsLogger>.Instance,
            service);

        await logger.LogUploadAsync(
            "source.mp4",
            "My title",
            "abc123",
            "https://youtube.test/watch?v=abc123",
            "published",
            DateTimeOffset.Parse("2026-03-28T10:00:00+00:00"),
            CancellationToken.None);

        await logger.LogUploadAsync(
            "source2.mp4",
            "My title 2",
            "def456",
            "https://youtube.test/watch?v=def456",
            "scheduled",
            null,
            CancellationToken.None);

        Assert.Equal(7, calls.Count);

        var batchUpdates = calls.Where(c => c.Method == HttpMethod.Post && c.Url.Contains(":batchUpdate", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, batchUpdates.Length);

        var addSheetDoc = JsonDocument.Parse(batchUpdates[0].Body!);
        Assert.Equal("Audit", addSheetDoc.RootElement.GetProperty("requests")[0].GetProperty("addSheet").GetProperty("properties").GetProperty("title").GetString());

        var normalizeDoc = JsonDocument.Parse(batchUpdates[1].Body!);
        var requests = normalizeDoc.RootElement.GetProperty("requests").EnumerateArray().ToArray();

        Assert.True(requests[0].TryGetProperty("insertDimension", out var insertRows));
        Assert.Equal("ROWS", insertRows.GetProperty("range").GetProperty("dimension").GetString());
        Assert.True(requests[1].TryGetProperty("insertDimension", out var insertCols));
        Assert.Equal("COLUMNS", insertCols.GetProperty("range").GetProperty("dimension").GetString());
        Assert.Equal(2, requests[2].GetProperty("appendDimension").GetProperty("length").GetInt32());
        Assert.True(requests.Any(r => r.TryGetProperty("clearBasicFilter", out _)));
        Assert.True(requests.Any(r => r.TryGetProperty("updateCells", out _)));
        Assert.True(requests.Any(r => r.TryGetProperty("setBasicFilter", out _)));

        var appendDoc1 = JsonDocument.Parse(batchUpdates[2].Body!);
        Assert.True(appendDoc1.RootElement.GetProperty("requests")[0].TryGetProperty("appendCells", out var appendCells1));
        var appendedRowValues = appendCells1.GetProperty("rows")[0].GetProperty("values").EnumerateArray().ToArray();
        Assert.Equal(10, appendedRowValues.Length);

        var youtubeCell = appendedRowValues[5].GetProperty("userEnteredValue");
        Assert.Contains("HYPERLINK", youtubeCell.GetProperty("formulaValue").GetString(), StringComparison.Ordinal);

        var appendDoc2 = JsonDocument.Parse(batchUpdates[3].Body!);
        Assert.True(appendDoc2.RootElement.GetProperty("requests")[0].TryGetProperty("appendCells", out _));
    }

    [Fact]
    public async Task LogUploadAsync_WhenSpreadsheetIdMissing_DoesNotCallApi()
    {
        var calls = new List<RecordedCall>();
        var handler = new RecordingHandler(request =>
        {
            calls.Add(new RecordedCall(request.Method, request.RequestUri!.ToString(), null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var service = CreateSheetsService(handler);
        var sheetsOptions = new StaticOptionsMonitor<GoogleSheetsOptions>(new GoogleSheetsOptions { SpreadsheetId = null });
        var driveOptions = new StaticOptionsMonitor<DriveOptions>(new DriveOptions());

        var logger = new GoogleSheetsLogger(
            sheetsOptions,
            driveOptions,
            NullLogger<GoogleSheetsLogger>.Instance,
            service);

        await logger.LogUploadAsync(
            "source.mp4",
            "My title",
            "abc123",
            "https://youtube.test/watch?v=abc123",
            "published",
            null,
            CancellationToken.None);

        Assert.Empty(calls);
    }

    [Fact]
    public async Task LogUploadAsync_WhenValuesGetFails_DoesNotThrow()
    {
        var calls = new List<RecordedCall>();

        var handler = new RecordingHandler(async request =>
        {
            var body = await ReadBodyAsync(request.Content);
            calls.Add(new RecordedCall(request.Method, request.RequestUri!.ToString(), body));

            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && IsSpreadsheetGet(url))
            {
                return Json("""
                {
                  "sheets": [
                    {
                      "properties": { "title": "Audit", "sheetId": 42, "gridProperties": { "columnCount": 10 } },
                      "conditionalFormats": []
                    }
                  ]
                }
                """);
            }

            if (request.Method == HttpMethod.Get && url.Contains("/values/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"error":{"code":403,"message":"forbidden"}}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"unexpected request"}""")
            };
        });

        var service = CreateSheetsService(handler);
        var sheetsOptions = new StaticOptionsMonitor<GoogleSheetsOptions>(new GoogleSheetsOptions
        {
            SpreadsheetId = "spreadsheet123",
            SheetName = "Audit"
        });
        var driveOptions = new StaticOptionsMonitor<DriveOptions>(new DriveOptions());

        var logger = new GoogleSheetsLogger(
            sheetsOptions,
            driveOptions,
            NullLogger<GoogleSheetsLogger>.Instance,
            service);

        await logger.LogUploadAsync(
            "source.mp4",
            "My title",
            "abc123",
            "https://youtube.test/watch?v=abc123",
            "published",
            null,
            CancellationToken.None);

        Assert.True(calls.Count >= 2);
    }

    [Fact]
    public async Task LogUploadAsync_WhenBatchUpdateFails_DoesNotThrow()
    {
        var calls = new List<RecordedCall>();
        var handler = new RecordingHandler(async request =>
        {
            var body = await ReadBodyAsync(request.Content);
            calls.Add(new RecordedCall(request.Method, request.RequestUri!.ToString(), body));

            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Get && IsSpreadsheetGet(url))
            {
                return Json("""
                {
                  "sheets": [
                    {
                      "properties": { "title": "Audit", "sheetId": 42, "gridProperties": { "columnCount": 10 } },
                      "conditionalFormats": []
                    }
                  ]
                }
                """);
            }

            if (request.Method == HttpMethod.Get && url.Contains("/values/", StringComparison.Ordinal))
            {
                return Json("""{"values":[["ts_utc","channel","source_file","title","youtube_id","youtube_url","status","scheduled_at_utc","quota_used","notes"]]}""");
            }

            if (request.Method == HttpMethod.Post && url.Contains(":batchUpdate", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"error":{"code":500,"message":"boom"}}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":"unexpected request"}""")
            };
        });

        var service = CreateSheetsService(handler);
        var sheetsOptions = new StaticOptionsMonitor<GoogleSheetsOptions>(new GoogleSheetsOptions
        {
            SpreadsheetId = "spreadsheet123",
            SheetName = "Audit"
        });
        var driveOptions = new StaticOptionsMonitor<DriveOptions>(new DriveOptions());

        var logger = new GoogleSheetsLogger(
            sheetsOptions,
            driveOptions,
            NullLogger<GoogleSheetsLogger>.Instance,
            service);

        await logger.LogUploadAsync(
            "source.mp4",
            "My title",
            "abc123",
            "https://youtube.test/watch?v=abc123",
            "published",
            null,
            CancellationToken.None);

        Assert.True(calls.Any(c => c.Method == HttpMethod.Post && c.Url.Contains(":batchUpdate", StringComparison.Ordinal)));
    }

    private static SheetsService CreateSheetsService(HttpMessageHandler handler)
    {
        return new SheetsService(new BaseClientService.Initializer
        {
            ApplicationName = "TubePilot.Tests",
            HttpClientFactory = new FixedHttpClientFactory(handler)
        });
    }

    private static bool IsSpreadsheetGet(string url)
        => url.Contains("sheets.googleapis.com/v4/spreadsheets/", StringComparison.Ordinal) &&
           !url.Contains("/values/", StringComparison.Ordinal) &&
           !url.Contains(":batchUpdate", StringComparison.Ordinal);

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

    private static async Task<string?> ReadBodyAsync(HttpContent? content)
    {
        if (content is null)
        {
            return null;
        }

        var bytes = await content.ReadAsByteArrayAsync();
        if (content.Headers.ContentEncoding.Any(static v => string.Equals(v, "gzip", StringComparison.OrdinalIgnoreCase)))
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        return Encoding.UTF8.GetString(bytes);
    }
}

file sealed record RecordedCall(HttpMethod Method, string Url, string? Body);

file sealed class FixedHttpClientFactory(HttpMessageHandler innerHandler) : Google.Apis.Http.IHttpClientFactory
{
    public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
    {
        var handler = new ConfigurableMessageHandler(innerHandler);
        var client = new ConfigurableHttpClient(handler);
        if (args.Initializers is not null)
        {
            foreach (var initializer in args.Initializers)
            {
                initializer.Initialize(client);
            }
        }

        return client;
    }
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
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => await responder(request);
}
