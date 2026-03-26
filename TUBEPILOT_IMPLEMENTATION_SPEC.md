# TubePilot: YouTube Upload Implementation Spec

## Current State

The project has:
- ✅ Google Drive polling (Service Account) — downloads new videos, tracks processed files
- ✅ Telegram bot UI — notifications, inline filter buttons, progress bar
- ⚠️ FFmpeg processing — **MOCK ONLY** (just copies files, no real FFmpeg commands)
- ❌ YouTube upload — **completely missing**
- ❌ Google Sheets logging — **missing**

## What Needs To Be Implemented

### 1. Real FFmpeg Processing (replace mock)

**Current mock in `FfmpegVideoProcessor.cs`:**
```csharp
// This is fake — just delays and copies file
for (int i = 0; i <= 100; i += 5)
{
    await progressCallback(i);
    await Task.Delay(200, ct);
}
File.Copy(inputPath, outPath, overwrite: true);
```

**Replace with real FFmpeg subprocess calls.** All filters use `ffmpeg` CLI via `Process.Start()`.

**Filters to implement (each calls ffmpeg as subprocess):**

| Filter | FFmpeg command | Notes |
|--------|---------------|-------|
| Mirror (hflip) | `-vf hflip -c:a copy` | Simple, no re-encode of audio |
| Volume -10-20% | `-af volume=0.85 -c:v copy` | Random 0.80-0.90, video stream copy |
| Slow down 4-7% | `-filter_complex "[0:v]setpts=1.05*PTS[v];[0:a]atempo=0.952[a]" -map [v] -map [a]` | Random factor 1.04-1.07 |
| Speed up 3-5% | `-filter_complex "[0:v]setpts=0.97*PTS[v];[0:a]atempo=1.03[a]" -map [v] -map [a]` | Random factor 1.03-1.05 |
| Color correction | `-vf "eq=saturation=1.05:brightness=-0.05:gamma=0.98" -c:a copy` | Random ranges per param |
| Slice 2:30-3:10 | `-ss {start} -t {duration} -c copy` | Random duration 150-190s per segment |
| Slice 5:10-7:10 | `-ss {start} -t {duration} -c copy` | Random duration 310-430s per segment |
| QR overlay | `-i qr.png -filter_complex "[1:v]scale=iw*0.25:-1[qr];[0:v][qr]overlay=W-w-20:H-h-20:enable='between(t,0,10)'"` | First 10 seconds only |
| Rotate 3-5° | `-vf "scale=iw*1.12:ih*1.12,rotate=0.07:fillcolor=black,crop=iw/1.12:ih/1.12"` | Zoom to hide black bars |
| Downscale 1080p | `-vf "scale=-2:1080"` | Only if source > 1080p |

**Combined single-pass processing:**
When multiple filters are selected, combine all video filters into one `-filter_complex` and all audio filters into one chain. This means ONE re-encode, not multiple passes.

Example (mirror + color + volume):
```
ffmpeg -y -i input.mp4 \
  -filter_complex "[0:v]hflip,eq=saturation=1.05:brightness=-0.05[vout];[0:a]volume=0.85[aout]" \
  -map [vout] -map [aout] \
  -c:v libx264 -crf 23 -preset medium -c:a aac \
  output.mp4
```

**Cut is always stream-copy** (no re-encode, fast):
```
ffmpeg -y -i input.mp4 -ss 0 -t 180 -c copy segment_01.mp4
```

If cut + filters: first cut (stream copy), then apply filters to each segment (re-encode).

**Progress reporting:** Parse FFmpeg's `-progress pipe:1` output to get `out_time_us`, calculate percentage from total duration, report via callback.

**Reference implementation:** See `video-bot-project/video_processor.py` in the same repo — it has all FFmpeg commands working in Python. Port the logic to C# subprocess calls.

---

### 2. YouTube Upload Service (create from scratch)

**Authentication:** YouTube Data API v3 requires OAuth2 user credentials (NOT service account).

**Credentials needed:**
```
GCP_CLIENT_ID     — from client_secrets.json (OAuth 2.0 Client ID, Desktop App type)
GCP_CLIENT_SECRET — from client_secrets.json
REFRESH_TOKEN     — obtained via OAuth2 flow (get_token.py script)
```

**⚠️ IMPORTANT: Service Account JSON CANNOT be used for YouTube upload.** YouTube API does not support service account authentication for uploading videos. Only OAuth2 with a real user's refresh token works.

**Token refresh flow:**
```
POST https://oauth2.googleapis.com/token
Content-Type: application/x-www-form-urlencoded

client_id={CLIENT_ID}
&client_secret={CLIENT_SECRET}
&refresh_token={REFRESH_TOKEN}
&grant_type=refresh_token

→ Response: { "access_token": "ya29.xxx", "expires_in": 3599 }
```
Access token lives ~1 hour. Refresh before each upload or cache with expiry check.

**Upload flow (resumable upload):**

Step 1: Initiate resumable upload
```
POST https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status
Authorization: Bearer {access_token}
Content-Type: application/json

{
  "snippet": {
    "title": "Video Title",
    "description": "Video description\n\n#tag1 #tag2",
    "tags": ["tag1", "tag2"],
    "categoryId": "22",
    "defaultLanguage": "en"
  },
  "status": {
    "privacyStatus": "public",
    "selfDeclaredMadeForKids": false
  }
}

→ Response Header: Location: {upload_url}
```

For scheduled publishing:
```json
{
  "status": {
    "privacyStatus": "private",
    "publishAt": "2026-04-01T10:00:00Z",
    "selfDeclaredMadeForKids": false
  }
}
```
YouTube auto-publishes at `publishAt` time and switches to public.

Step 2: Upload video file in chunks
```
PUT {upload_url}
Content-Type: video/mp4
Content-Length: {chunk_size}

[binary data, 4MB chunks]

→ Final response: { "id": "xxxVideoId", ... }
```

Step 3: Upload thumbnail (optional)
```
POST https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={video_id}
Authorization: Bearer {access_token}
Content-Type: image/jpeg

[thumbnail binary]
```

**YouTube API Quotas:**
- Upload: 1600 units per video
- Thumbnail: 50 units
- Daily limit: 10,000 units per GCP project ≈ 6 uploads/day
- Quota resets at 00:00 Pacific Time

**Retry logic:**
- 401 (token expired): refresh access token, retry
- 403 (quota exceeded): stop, alert via Telegram, retry next day
- 500/503 (server error): retry 3x with backoff (1min → 5min → 15min)

**Interface:**
```csharp
public interface IYouTubeUploader
{
    Task<UploadResult> UploadVideoAsync(
        string videoPath,
        string title,
        string description,
        string[] tags,
        string? thumbnailPath = null,
        DateTime? scheduledAt = null,
        string categoryId = "22",
        CancellationToken ct = default
    );

    Task<ChannelInfo> GetChannelInfoAsync(CancellationToken ct = default);
}

public record UploadResult(string VideoId, string YouTubeUrl, int QuotaUsed);
public record ChannelInfo(string ChannelId, string Title);
```

**NuGet package:** `Google.Apis.YouTube.v3` — official Google library for .NET. Handles OAuth token refresh, resumable upload, and all API calls.

---

### 3. Telegram Bot: YouTube Publishing Flow

After video processing is complete, add publishing buttons and wizard:

**Flow:**
```
[After processing completes, for each result video/segment:]

Bot sends: thumbnail preview + video info (duration, size)
  Button: [📤 Publish to YouTube]

User taps [Publish] →
  Bot: "📺 Select channel:"
  Button: [247AI Test Channel]  (from API: channels.list mine=true)

User selects channel →
  Bot: "✏️ Enter video title:"

User types title →
  Bot: "📝 Enter description: (/skip to skip)"

User types description →
  Bot: "🏷 Enter tags (comma separated, /skip to skip):"

User types tags →
  Bot: "📅 When to publish?"
  Buttons: [Now] [Next free slot] [Pick date]

  If [Pick date]:
    Bot: "Enter date (YYYY-MM-DD HH:MM):"

User selects schedule →
  Bot: "⏳ Uploading to YouTube..."
  [resumable upload with progress %]
  Bot: "✅ Published! https://youtube.com/watch?v={id}"
```

**For segments (after cut), add:**
```
Button: [📤 Publish ALL segments]

If tapped:
  Bot: "✏️ Enter title template (use {N} for part number):"
  User: "Beach Carnival Part {N}"
  Bot: "📝 Enter description:"
  ...same flow...
  → Auto-increments schedule: Part 1 = today, Part 2 = tomorrow, etc.
```

**FSM States:**
```csharp
public enum PublishState
{
    Idle,
    WaitingForChannel,
    WaitingForTitle,
    WaitingForDescription,
    WaitingForTags,
    WaitingForSchedule,
    WaitingForCustomDate,
    Uploading
}
```

---

### 4. Google Sheets Audit Logging

Log every published video to Google Sheets for transparency.

**Authentication:** Service Account JSON (the `sheets-writer@...` key that already exists).

**NuGet:** `Google.Apis.Sheets.v4`

**Columns:**
| ts_utc | channel | source_file | title | youtube_id | youtube_url | status | scheduled_at | quota_used |
|--------|---------|-------------|-------|------------|-------------|--------|-------------|------------|

**When to log:**
- After successful upload: status = `published` or `scheduled`
- After failed upload: status = `failed`, include error in notes

**Interface:**
```csharp
public interface ISheetsLogger
{
    Task LogUploadAsync(
        string channel,
        string sourceFile,
        string title,
        string youtubeId,
        string youtubeUrl,
        string status,
        DateTime? scheduledAt,
        int quotaUsed,
        CancellationToken ct = default
    );
}
```

---

### 5. Scheduling Logic

**Rules:**
- Max 1 upload/day per channel (configurable)
- "Next free slot" = check last scheduled date for this channel, add 1 day
- Time = configured daily posting time (e.g., 10:00 in channel timezone)
- "Publish all segments" = auto-increment: Part 1 today, Part 2 tomorrow, etc.
- Track scheduled dates in local state (JSON file or in-memory dict)

---

## Configuration (secrets.json)

Update `secrets.json` to include all credentials:

```json
{
  "GoogleDrive": {
    "FolderId": "xxx",
    "DownloadDirectory": "downloads",
    "ProcessedDirectory": "processed",
    "PollingIntervalSeconds": 30,
    "ServiceAccount": {
      "type": "service_account",
      "project_id": "yt-autoposter-dev",
      "private_key_id": "...",
      "private_key": "-----BEGIN PRIVATE KEY-----\n...",
      "client_email": "sheets-writer@yt-autoposter-dev.iam.gserviceaccount.com",
      "client_id": "...",
      "auth_uri": "https://accounts.google.com/o/oauth2/auth",
      "token_uri": "https://oauth2.googleapis.com/token",
      "auth_provider_x509_cert_url": "...",
      "client_x509_cert_url": "..."
    }
  },
  "Telegram": {
    "BotToken": "xxx",
    "AllowedChatId": 123456789
  },
  "YouTube": {
    "ClientId": "xxx.apps.googleusercontent.com",
    "ClientSecret": "GOCSPX-xxx",
    "RefreshToken": "1//0xxx",
    "DefaultCategoryId": "22",
    "MaxUploadsPerDay": 6
  },
  "GoogleSheets": {
    "SpreadsheetId": "xxx",
    "SheetName": "Audit"
  }
}
```

**Note:** Google Sheets uses the SAME Service Account as Drive (`sheets-writer@...`). Share the Sheets document with this email as Editor.

---

## Files You Need To Provide

```
✅ client_secrets.json            → YouTube section in secrets.json (ClientId + ClientSecret)
✅ service_account.json           → Already in secrets.json under GoogleDrive.ServiceAccount
✅ REFRESH_TOKEN                  → YouTube.RefreshToken in secrets.json
✅ TELEGRAM_BOT_TOKEN             → Already in secrets.json
✅ GDRIVE_FOLDER_ID               → Already in secrets.json
✅ GSHEETS_SPREADSHEET_ID         → GoogleSheets.SpreadsheetId in secrets.json
```

All credentials go into ONE file: `secrets.json`. No separate .env needed.

---

## Implementation Priority

1. **Real FFmpeg processing** — replace mock with actual subprocess calls
2. **YouTube upload service** — OAuth2 token refresh + resumable upload + thumbnail
3. **Telegram publishing flow** — channel selection → metadata → schedule → upload
4. **Google Sheets logging** — audit row per upload
5. **Scheduling logic** — next free slot, auto-increment for segments

---

## NuGet Packages to Add

```xml
<PackageReference Include="Google.Apis.YouTube.v3" Version="1.73.0.xxxxx" />
<PackageReference Include="Google.Apis.Sheets.v4" Version="1.73.0.xxxxx" />
```

Both are official Google .NET libraries that handle OAuth token refresh, resumable uploads, and API calls.
