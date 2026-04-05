using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;

namespace TubePilot.Worker;

public static class ProcessedVideoEndpoints
{
    public static void MapRoutes(IEndpointRouteBuilder endpoints, string processedDirectory)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(processedDirectory);

        var processedDirectoryPath = Path.GetFullPath(processedDirectory);
        Directory.CreateDirectory(processedDirectoryPath);
        var contentTypeProvider = CreateContentTypeProvider();

        endpoints.MapGet("/play/{filename}", (string filename) =>
        {
            var filePath = TryResolveFilePath(processedDirectoryPath, filename);
            if (filePath is null)
            {
                return Results.NotFound();
            }

            var contentType = GetContentType(contentTypeProvider, filePath);
            var watchUrl = $"/video/{Uri.EscapeDataString(Path.GetFileName(filePath))}";
            var html = BuildPlayerHtml(Path.GetFileName(filePath), watchUrl, contentType);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        endpoints.MapGet("/video/{filename}", (string filename) =>
        {
            var filePath = TryResolveFilePath(processedDirectoryPath, filename);
            if (filePath is null)
            {
                return Results.NotFound();
            }

            return Results.File(
                filePath,
                GetContentType(contentTypeProvider, filePath),
                enableRangeProcessing: true);
        });
    }

    internal static string? TryResolveFilePath(string processedDirectoryPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(processedDirectoryPath) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var trimmedFileName = fileName.Trim();
        if (trimmedFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var normalizedFileName = Path.GetFileName(trimmedFileName);
        if (!string.Equals(trimmedFileName, normalizedFileName, StringComparison.Ordinal))
        {
            return null;
        }

        var rootPath = EnsureTrailingSeparator(Path.GetFullPath(processedDirectoryPath));
        var candidatePath = Path.GetFullPath(Path.Combine(rootPath, normalizedFileName));
        if (!candidatePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(candidatePath) ? candidatePath : null;
    }

    internal static string BuildPlayerHtml(string fileName, string watchUrl, string contentType)
    {
        var encodedFileName = WebUtility.HtmlEncode(fileName);
        var encodedWatchUrl = WebUtility.HtmlEncode(watchUrl);
        var encodedContentType = WebUtility.HtmlEncode(contentType);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{{encodedFileName}} — TubePilot Player</title>
    <style>
        body { margin: 0; font-family: Arial, sans-serif; background: #111827; color: #f9fafb; }
        main { max-width: 960px; margin: 0 auto; padding: 24px; }
        h1 { font-size: 1.5rem; margin-bottom: 12px; word-break: break-word; }
        video { width: 100%; max-height: 80vh; border-radius: 12px; background: #000; }
        a { color: #93c5fd; }
        p { color: #d1d5db; }
    </style>
</head>
<body>
    <main>
        <h1>{{encodedFileName}}</h1>
        <video controls playsinline preload="metadata">
            <source src="{{encodedWatchUrl}}" type="{{encodedContentType}}">
            Your browser does not support the video tag.
        </video>
        <p><a href="{{encodedWatchUrl}}">Open raw stream</a></p>
    </main>
</body>
</html>
""";
    }

    private static FileExtensionContentTypeProvider CreateContentTypeProvider()
    {
        var contentTypeProvider = new FileExtensionContentTypeProvider();
        contentTypeProvider.Mappings[".mp4"] = "video/mp4";
        contentTypeProvider.Mappings[".webm"] = "video/webm";
        contentTypeProvider.Mappings[".mkv"] = "video/x-matroska";
        contentTypeProvider.Mappings[".avi"] = "video/x-msvideo";
        contentTypeProvider.Mappings[".mov"] = "video/quicktime";
        return contentTypeProvider;
    }

    private static string GetContentType(FileExtensionContentTypeProvider contentTypeProvider, string filePath)
        => contentTypeProvider.TryGetContentType(filePath, out var contentType)
            ? contentType
            : "application/octet-stream";

    private static string EnsureTrailingSeparator(string directoryPath)
        => directoryPath.EndsWith(Path.DirectorySeparatorChar)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;
}
