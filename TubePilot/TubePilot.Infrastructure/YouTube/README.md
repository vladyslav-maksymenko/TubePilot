# YouTube uploader (Issue #4)

This module implements `TubePilot.Core.Contracts.IYouTubeUploader` using:
- OAuth2 refresh token (`YouTube:ClientId`, `YouTube:ClientSecret`, `YouTube:RefreshToken`)
- resumable uploads (chunked `PUT` with `Content-Range`)
- optional thumbnail upload
- scheduled publishing (`publishAt` + `privacyStatus=private`)

## Config

Put secrets in `TubePilot/TubePilot.Worker/secrets.json` (do not commit):

```json
{
  "YouTube": {
    "ClientId": "xxx.apps.googleusercontent.com",
    "ClientSecret": "GOCSPX-xxx",
    "RefreshToken": "1//xxx",
    "DefaultCategoryId": "22",
    "MaxUploadsPerDay": 6
  }
}
```

## Manual smoke test (code snippet)

Call the uploader from any async context (e.g. a `BackgroundService`):

```csharp
var result = await youTubeUploader.UploadAsync(
    new YouTubeUploadRequest(
        VideoFilePath: @"C:\videos\test.mp4",
        Title: "TubePilot test",
        Description: "Uploaded by TubePilot",
        Tags: ["test", "tubepilot"],
        ScheduledPublishAtUtc: DateTimeOffset.UtcNow.AddHours(2),
        ThumbnailFilePath: @"C:\videos\thumb.jpg"),
    progress =>
    {
        logger.LogInformation("YouTube upload progress: {Percent}%", progress);
        return Task.CompletedTask;
    },
    ct);

logger.LogInformation("YouTube result: {Url} ({Status})", result.YouTubeUrl, result.Status);
```
