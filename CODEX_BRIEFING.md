# TubePilot — Codex Briefing

## Project Context

TubePilot is a YouTube auto-posting system for a client (Igor) who manages 50+ YouTube channels. The system automates: video processing (mirror, cut, color correction, etc.), publishing via official YouTube Data API v3, scheduling, and audit logging.

**History:**
- Before us, another team built a Python bot for the client (`video-bot-project/` folder in this repo). It works but has limitations — YouTube publishing goes through a third-party service (dohoo.ai) instead of direct YouTube API.
- Our team (247AI) took over. A colleague (Vlad) started rebuilding the system in .NET 10 — that's the `TubePilot/` folder. He built the foundation: Google Drive polling, Telegram bot UI with inline filter buttons, and progress reporting.
- Now we need to finish the implementation: real FFmpeg processing, YouTube upload via official API, publishing flow in Telegram, Google Sheets logging, and scheduling.

**End goal:** A Telegram bot that monitors Google Drive for new videos → offers processing options (mirror, cut, color, speed, etc.) → processes via FFmpeg → lets user publish to YouTube with title/description/tags/schedule → logs everything to Google Sheets. First we build MVP on 1 test channel, then scale to 50+ channels for the client.

---

## Repository Structure

```
TubePilot/                          ← .NET project (this is what we work on)
├── TubePilot.Core/                 ← Domain models, interfaces
│   ├── Contracts/
│   │   ├── IDriveWatcher.cs        ← ✅ Done
│   │   ├── ITelegramBotService.cs  ← ✅ Done
│   │   └── IVideoProcessor.cs     ← ✅ Interface exists, implementation is MOCK
│   ├── Domain/
│   │   └── DriveFile.cs            ← ✅ Done
│   └── Utils/
│       └── FileNameSanitizer.cs    ← ✅ Done
├── TubePilot.Infrastructure/       ← Implementations
│   ├── Drive/
│   │   ├── GoogleDriveWatcher.cs   ← ✅ Done (Service Account auth)
│   │   ├── Options/                ← ✅ Done
│   │   └── State/
│   │       └── KnownFilesStore.cs  ← ✅ Done (tracks downloaded files)
│   ├── Telegram/
│   │   ├── TelegramBotService.cs   ← ✅ Partial (UI works, publishing flow MISSING)
│   │   ├── Models/
│   │   │   └── VideoProcessingState.cs ← ✅ Done
│   │   └── Options/
│   │       └── TelegramOptions.cs  ← ✅ Done
│   └── Video/
│       └── FfmpegVideoProcessor.cs ← ⚠️ MOCK ONLY — needs real FFmpeg
├── TubePilot.Worker/               ← Entry point
│   ├── Program.cs                  ← ✅ Done
│   ├── Worker.cs                   ← ✅ Done (Drive polling loop)
│   ├── appsettings.json            ← Config (general, committed to git)
│   └── secrets.json                ← Config (secrets, NOT in git)
└── TubePilot.sln

video-bot-project/                  ← Python bot (previous team, REFERENCE ONLY)
├── bot.py                          ← Telegram bot with full publishing flow
├── video_processor.py              ← ✅ Real FFmpeg commands (use as reference)
├── drive_monitor.py                ← Drive polling
├── dohoo_publisher.py              ← YouTube via third-party (we replace with direct API)
├── config.py                       ← Config
└── main.py                         ← Entry point

Documentations/                     ← Test scenarios doc
```

---

## What's Done vs What Needs Work

| Component | Status | Details |
|-----------|--------|---------|
| Google Drive polling | ✅ Done | Service Account auth, downloads new videos, tracks via known_files.json |
| Telegram bot UI | ✅ Done | Notifications, inline filter buttons (10 options), progress bar |
| FFmpeg processing | ⚠️ MOCK | `FfmpegVideoProcessor.cs` just copies files. No real FFmpeg calls |
| YouTube upload | ❌ Missing | No interface, no implementation, no OAuth token handling |
| Telegram publish flow | ❌ Missing | No channel selection, title/desc/tags input, scheduling UI |
| Google Sheets logging | ❌ Missing | No interface, no implementation |
| Scheduling logic | ❌ Missing | No "next free slot", no auto-increment |

---

## Implementation Spec

Full technical specification is in **`TUBEPILOT_IMPLEMENTATION_SPEC.md`** (in the repo or provided separately). It covers:

1. **Real FFmpeg processing** — exact commands for all 10 filters, single-pass combined processing, progress reporting
2. **YouTube upload service** — OAuth2 token refresh, resumable upload, thumbnail upload, quota management, retry logic
3. **Telegram publishing wizard** — FSM states, full flow from channel selection to upload confirmation
4. **Google Sheets audit logging** — columns, service account auth, when to log
5. **Scheduling logic** — next free slot, auto-increment for segments

**Read this file first before starting implementation.**

---

## Configuration

The project uses two config files that .NET auto-merges at runtime:

### `appsettings.json` (general settings, committed to git)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "GoogleDrive": {
    "PollingIntervalSeconds": 30,
    "DownloadDirectory": "downloads",
    "ProcessedDirectory": "processed"
  },
  "Telegram": {
    "BaseUrl": "http://localhost:5000"
  },
  "YouTube": {
    "DefaultCategoryId": "22",
    "MaxUploadsPerDay": 6
  },
  "GoogleSheets": {
    "SheetName": "Audit"
  }
}
```

### `secrets.json` (secrets, NOT in git, NOT committed)

Repo provides `TubePilot/TubePilot.Worker/secrets.example.json` as a template. Copy it to `TubePilot/TubePilot.Worker/secrets.json` (local-only).

```json
{
  "GoogleDrive": {
    "FolderId": "REAL_FOLDER_ID",
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
    "BotToken": "REAL_BOT_TOKEN",
    "AllowedChatId": 721125811
  },
  "YouTube": {
    "ClientId": "REAL_CLIENT_ID.apps.googleusercontent.com",
    "ClientSecret": "GOCSPX-REAL_SECRET",
    "RefreshToken": "1//REAL_REFRESH_TOKEN"
  },
  "GoogleSheets": {
    "SpreadsheetId": "REAL_SPREADSHEET_ID"
  }
}
```

.NET merges both at startup — `secrets.json` overrides `appsettings.json` for matching keys.

**⚠️ YouTube/GoogleSheets config is split:** non-secret defaults live in `appsettings.json`, credentials/ids live in `secrets.json`.

---

## Authentication Summary

| Service | Auth Method | Credentials |
|---------|------------|-------------|
| Google Drive | Service Account | `secrets.json` → `GoogleDrive.ServiceAccount` block |
| Google Sheets | Service Account (same) | Same service account. Share the spreadsheet with `client_email` as Editor |
| YouTube upload | **OAuth2 (NOT service account!)** | `secrets.json` → `YouTube.ClientId` + `ClientSecret` + `RefreshToken` |
| Telegram | Bot token | `secrets.json` → `Telegram.BotToken` |

**CRITICAL: YouTube Data API does NOT support Service Account auth for uploading videos. Only OAuth2 with a real user's refresh token works. Do NOT try to use the service account JSON for YouTube.**

---

## Reference Code

The `video-bot-project/` folder contains a working Python implementation by the previous team. Use it as reference for:

- **FFmpeg commands:** `video-bot-project/video_processor.py` — all 10 filters with exact FFmpeg CLI args, single-pass combined processing, progress reporting. Port this logic to C# subprocess calls.
- **Telegram UX:** `video-bot-project/bot.py` — publishing flow (channel selection → title → description → upload), result messages with thumbnails, progress updates.
- **YouTube publishing:** `video-bot-project/dohoo_publisher.py` — uses third-party API (we replace with direct YouTube API), but the flow/UX is the same.

---

## Implementation Priority

Start in this order:

1. **Real FFmpeg processing** — replace mock in `FfmpegVideoProcessor.cs` with real subprocess calls. Reference: `video-bot-project/video_processor.py`. Test: select mirror → run → verify output is actually mirrored (not just copied).

2. **YouTube upload service** — create `IYouTubeUploader` interface + `YouTubeUploadService` implementation. OAuth2 token refresh → resumable upload → thumbnail. Test: upload a test video to the test channel.

3. **Telegram publishing flow** — add FSM states to `TelegramBotService.cs` for: channel selection → title → description → tags → schedule → upload → confirmation. Test: full flow through bot.

4. **Google Sheets logging** — create `ISheetsLogger` interface + implementation. Log every upload. Test: verify row appears in spreadsheet.

5. **Scheduling logic** — "next free slot" = last scheduled date + 1 day. "Publish all segments" = auto-increment dates. Test: publish 3 segments → verify dates are consecutive.

---

## How to Run

```bash
cd TubePilot/TubePilot.Worker
dotnet run
```

Requires:
- .NET 10 SDK
- FFmpeg installed and in PATH
- `secrets.json` filled with real credentials
- Google Drive folder shared with service account email
- Google Sheets shared with service account email
- Telegram bot created via @BotFather

---

## Key Technical Constraints

1. **Resumable upload is mandatory** for YouTube — standard upload fails on files >100MB. Always use `uploadType=resumable` with 4MB chunks.
2. **Access token lives ~1 hour** — always check expiry before upload, refresh from refresh_token if expired.
3. **YouTube quota: 10,000 units/day/project** — upload = 1600 units ≈ 6 uploads/day. Track usage.
4. **scheduledPublish requires `privacyStatus: "private"`** — otherwise YouTube ignores `publishAt`.
5. **Cut is stream-copy** (`-c copy`, fast, no quality loss). Any filter (mirror, color, speed) forces re-encode (`libx264 + aac`).
6. **Combined single-pass** — when multiple filters selected, chain all into one `-filter_complex`. One re-encode, not multiple passes.
7. **Service Account for Drive/Sheets, OAuth2 for YouTube** — do not mix them up.
