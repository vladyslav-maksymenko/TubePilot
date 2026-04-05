using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TubePilot.Core.Contracts;
using TubePilot.Infrastructure.YouTube.Options;

namespace TubePilot.Infrastructure.YouTube;

internal sealed class YouTubeUploader(
    HttpClient httpClient,
    IYouTubeAccessTokenProvider accessTokenProvider,
    IOptionsMonitor<YouTubeOptions> optionsMonitor,
    ILogger<YouTubeUploader> logger) : IYouTubeUploader
{
    private const int ChunkSizeBytes = 4 * 1024 * 1024;

    public async Task<YouTubeUploadResult> UploadAsync(
        YouTubeUploadRequest request,
        Func<int, Task> progressCallback,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progressCallback);

        if (!File.Exists(request.VideoFilePath))
        {
            throw new FileNotFoundException("Video file not found.", request.VideoFilePath);
        }

        await progressCallback(0);

        var accessToken = request.Credentials is not null
            ? await accessTokenProvider.GetAccessTokenAsync(request.Credentials, ct)
            : await accessTokenProvider.GetAccessTokenAsync(ct);
        var uploadUrl = await InitiateResumableUploadAsync(request, accessToken, ct);
        var videoId = await UploadVideoInChunksAsync(request, uploadUrl, accessToken, progressCallback, ct);

        if (!string.IsNullOrWhiteSpace(request.ThumbnailFilePath))
        {
            await TryUploadThumbnailAsync(videoId, request.ThumbnailFilePath!, accessToken, ct);
        }

        var scheduled = request.ScheduledPublishAtUtc?.ToUniversalTime();
        return new YouTubeUploadResult(
            videoId,
            $"https://www.youtube.com/watch?v={videoId}",
            scheduled is null ? YouTubeUploadStatus.Published : YouTubeUploadStatus.Scheduled,
            scheduled);
    }

    private async Task<Uri> InitiateResumableUploadAsync(YouTubeUploadRequest request, string accessToken, CancellationToken ct)
    {
        var options = optionsMonitor.CurrentValue;
        var payload = BuildInitiatePayload(request, options);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var fileInfo = new FileInfo(request.VideoFilePath);
        var contentType = GuessMimeType(request.VideoFilePath) ?? "application/octet-stream";

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status");

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        httpRequest.Headers.TryAddWithoutValidation("X-Upload-Content-Length", fileInfo.Length.ToString());
        httpRequest.Headers.TryAddWithoutValidation("X-Upload-Content-Type", contentType);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "YouTube resumable upload initiation failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
                (int)response.StatusCode,
                response.ReasonPhrase,
                responseBody);

            throw new HttpRequestException($"YouTube resumable upload initiation failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        if (!response.Headers.Location?.IsAbsoluteUri ?? true)
        {
            throw new InvalidOperationException("YouTube resumable upload initiation response is missing Location header.");
        }

        return response.Headers.Location!;
    }

    private async Task<string> UploadVideoInChunksAsync(
        YouTubeUploadRequest request,
        Uri uploadUrl,
        string accessToken,
        Func<int, Task> progressCallback,
        CancellationToken ct)
    {
        await using var fileStream = new FileStream(
            request.VideoFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 64,
            useAsync: true);

        var totalBytes = fileStream.Length;
        var bytesUploaded = 0L;
        var lastReportedPercent = -1;

        var buffer = new byte[ChunkSizeBytes];
        while (bytesUploaded < totalBytes)
        {
            ct.ThrowIfCancellationRequested();

            fileStream.Position = bytesUploaded;
            var bytesRead = await fileStream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, totalBytes - bytesUploaded), ct);
            if (bytesRead <= 0)
            {
                break;
            }

            var startByte = bytesUploaded;
            var endByte = bytesUploaded + bytesRead - 1;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var chunkContent = new ByteArrayContent(buffer, 0, bytesRead);
            chunkContent.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(request.VideoFilePath) ?? "application/octet-stream");
            chunkContent.Headers.ContentRange = new ContentRangeHeaderValue(startByte, endByte, totalBytes);
            httpRequest.Content = chunkContent;

            using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode == 308)
            {
                bytesUploaded = endByte + 1;

                if (response.Headers.TryGetValues("Range", out var rangeValues))
                {
                    var range = rangeValues.FirstOrDefault();
                    var lastByte = TryParseLastByteFromRangeHeader(range);
                    if (lastByte is not null && lastByte.Value + 1 > bytesUploaded)
                    {
                        bytesUploaded = lastByte.Value + 1;
                    }
                }

                lastReportedPercent = await ReportProgressAsync(bytesUploaded, totalBytes, progressCallback, lastReportedPercent);
                continue;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "YouTube resumable upload chunk failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    responseBody);

                throw new HttpRequestException($"YouTube upload failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("id", out var idElement) ||
                idElement.GetString() is not { Length: > 0 } videoId)
            {
                throw new InvalidOperationException("YouTube upload response is missing video id.");
            }

            bytesUploaded = totalBytes;
            await progressCallback(100);
            return videoId;
        }

        throw new InvalidOperationException("YouTube upload ended unexpectedly before completion.");
    }

    private async Task TryUploadThumbnailAsync(string videoId, string thumbnailFilePath, string accessToken, CancellationToken ct)
    {
        if (!File.Exists(thumbnailFilePath))
        {
            logger.LogWarning("Thumbnail file not found: {ThumbnailFilePath}", thumbnailFilePath);
            return;
        }

        await using var stream = new FileStream(thumbnailFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, useAsync: true);

        var url = $"https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(videoId)}&uploadType=media";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(GuessMimeType(thumbnailFilePath) ?? "application/octet-stream");
        httpRequest.Content = content;

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "YouTube thumbnail upload failed: {StatusCode} {ReasonPhrase}. Body: {Body}",
                (int)response.StatusCode,
                response.ReasonPhrase,
                responseBody);
        }
    }

    private static object BuildInitiatePayload(YouTubeUploadRequest request, YouTubeOptions options)
    {
        var scheduledUtc = request.ScheduledPublishAtUtc?.UtcDateTime;
        var privacyStatus = scheduledUtc is null ? MapVisibility(request.Visibility) : "private";
        var categoryId = string.IsNullOrWhiteSpace(request.CategoryId) ? options.DefaultCategoryId : request.CategoryId;

        return new
        {
            snippet = new
            {
                title = request.Title,
                description = request.Description,
                tags = request.Tags,
                categoryId
            },
            status = new
            {
                privacyStatus,
                publishAt = scheduledUtc,
                selfDeclaredMadeForKids = false
            }
        };
    }

    private static string MapVisibility(YouTubeVideoVisibility visibility)
        => visibility switch
        {
            YouTubeVideoVisibility.Public => "public",
            YouTubeVideoVisibility.Unlisted => "unlisted",
            YouTubeVideoVisibility.Private => "private",
            _ => "public"
        };

    private static async Task<int> ReportProgressAsync(
        long bytesUploaded,
        long totalBytes,
        Func<int, Task> progressCallback,
        int lastReportedPercent)
    {
        if (totalBytes <= 0)
        {
            return lastReportedPercent;
        }

        var percent = (int)Math.Floor(bytesUploaded * 100d / totalBytes);
        if (percent >= 100)
        {
            percent = 99;
        }

        if (percent == lastReportedPercent)
        {
            return lastReportedPercent;
        }

        await progressCallback(percent);
        return percent;
    }

    private static long? TryParseLastByteFromRangeHeader(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return null;
        }

        // Example: "bytes=0-1048575"
        var equalsIndex = rangeHeader.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex < 0 || equalsIndex == rangeHeader.Length - 1)
        {
            return null;
        }

        var dashIndex = rangeHeader.IndexOf('-', equalsIndex + 1);
        if (dashIndex < 0 || dashIndex == rangeHeader.Length - 1)
        {
            return null;
        }

        var lastByteText = rangeHeader[(dashIndex + 1)..].Trim();
        return long.TryParse(lastByteText, out var lastByte) ? lastByte : null;
    }

    private static string? GuessMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => null
        };
    }
}
