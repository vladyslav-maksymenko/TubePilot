# TubePilot

РЎРёСЃС‚РµРјР° Р°РІС‚РѕРїРѕСЃС‚РёРЅРіСѓ РІС–РґРµРѕ РЅР° YouTube, РєРµСЂРѕРІР°РЅР° С‡РµСЂРµР· Telegram-Р±РѕС‚Р°. РњРѕРЅС–С‚РѕСЂРёС‚СЊ Google Drive в†’ РѕР±СЂРѕР±Р»СЏС” РІС–РґРµРѕ С‡РµСЂРµР· FFmpeg в†’ РїСѓР±Р»С–РєСѓС” РЅР° YouTube в†’ Р»РѕРіСѓС” РІ Google Sheets.

---

## Project Context

TubePilot is a YouTube auto-posting system for a client (Igor) who manages 50+ YouTube channels. The system automates: video processing (mirror, cut, color correction, etc.), publishing via official YouTube Data API v3, scheduling, and audit logging.

**History:**
- Before us, another team built a Python bot for the client (`video-bot-project/` folder in this repo). It works but has limitations вЂ” YouTube publishing goes through a third-party service (dohoo.ai) instead of direct YouTube API.
- Our team (247AI) took over. A colleague (Vlad) started rebuilding the system in .NET 10 вЂ” that's the `TubePilot/` folder. He built the foundation: Google Drive polling, Telegram bot UI with inline filter buttons, and progress reporting.
- Now we need to finish the implementation: real FFmpeg processing, YouTube upload via official API, publishing flow in Telegram, Google Sheets logging, and scheduling.

**End goal:** A Telegram bot that monitors Google Drive for new videos в†’ offers processing options (mirror, cut, color, speed, etc.) в†’ processes via FFmpeg в†’ lets user publish to YouTube with title/description/tags/schedule в†’ logs everything to Google Sheets. First we build MVP on 1 test channel, then scale to 50+ channels for the client.

---

## Repository Structure

```
TubePilot/                          в†ђ .NET project (this is what we work on)
в”њв”Ђв”Ђ TubePilot.Core/                 в†ђ Domain models, interfaces
в”‚   в”њв”Ђв”Ђ Contracts/
в”‚   в”‚   в”њв”Ђв”Ђ IDriveWatcher.cs        в†ђ вњ… Done
в”‚   в”‚   в”њв”Ђв”Ђ ITelegramBotService.cs  в†ђ вњ… Done
в”‚   в”‚   в”њв”Ђв”Ђ IVideoProcessor.cs     в†ђ вњ… Done
в”‚   в”‚   в””в”Ђв”Ђ IYouTubeUploader.cs    в†ђ вњ… Done
в”‚   в”њв”Ђв”Ђ Domain/
в”‚   в”‚   в””в”Ђв”Ђ DriveFile.cs            в†ђ вњ… Done
в”‚   в””в”Ђв”Ђ Utils/
в”‚       в””в”Ђв”Ђ FileNameSanitizer.cs    в†ђ вњ… Done
в”њв”Ђв”Ђ TubePilot.Infrastructure/       в†ђ Implementations
в”‚   в”њв”Ђв”Ђ Drive/
в”‚   в”‚   в”њв”Ђв”Ђ GoogleDriveWatcher.cs   в†ђ вњ… Done (Service Account auth)
в”‚   в”‚   в”њв”Ђв”Ђ Options/                в†ђ вњ… Done
в”‚   в”‚   в””в”Ђв”Ђ State/
в”‚   в”‚       в””в”Ђв”Ђ KnownFilesStore.cs  в†ђ вњ… Done (tracks downloaded files)
в”‚   в”њв”Ђв”Ђ Telegram/
в”‚   в”‚   в”њв”Ђв”Ђ TelegramBotService.cs   в†ђ вњ… Done (UI + publishing wizard)
в”‚   в”‚   в”њв”Ђв”Ђ TelegramUploadJobRunner.cs в†ђ вњ… Done (YouTube upload orchestration)
в”‚   в”‚   в”њв”Ђв”Ђ Models/                 в†ђ вњ… Done
в”‚   в”‚   в””в”Ђв”Ђ Options/                в†ђ вњ… Done
в”‚   в”њв”Ђв”Ђ Video/
в”‚   в”‚   в””в”Ђв”Ђ FfmpegVideoProcessor.cs в†ђ вњ… Done (real FFmpeg processing)
в”‚   в”њв”Ђв”Ђ YouTube/
в”‚   в”‚   в”њв”Ђв”Ђ YouTubeUploader.cs      в†ђ вњ… Done (OAuth2 + resumable upload)
в”‚   в”‚   в””в”Ђв”Ђ OAuthRefreshTokenAccessTokenProvider.cs в†ђ вњ… Done
в”‚   в””в”Ђв”Ђ GoogleSheets/
в”‚       в””в”Ђв”Ђ GoogleSheetsLogger.cs   в†ђ вњ… Done
в”њв”Ђв”Ђ TubePilot.Worker/               в†ђ Entry point
в”‚   в”њв”Ђв”Ђ Program.cs                  в†ђ вњ… Done
в”‚   в”њв”Ђв”Ђ Worker.cs                   в†ђ вњ… Done (Drive polling loop)
в”‚   в”њв”Ђв”Ђ appsettings.json            в†ђ Config (general, committed to git)
в”‚   в””в”Ђв”Ђ secrets.json                в†ђ Config (secrets, NOT in git)
в”њв”Ђв”Ђ TubePilot.Infrastructure.Tests/ в†ђ Unit & integration tests
в”њв”Ђв”Ђ TubePilot.Worker.Tests/         в†ђ Endpoint tests
в””в”Ђв”Ђ TubePilot.sln

video-bot-project/                  в†ђ Python bot (previous team, REFERENCE ONLY)
в”њв”Ђв”Ђ bot.py                          в†ђ Telegram bot with full publishing flow
в”њв”Ђв”Ђ video_processor.py              в†ђ вњ… Real FFmpeg commands (use as reference)
в”њв”Ђв”Ђ drive_monitor.py                в†ђ Drive polling
в”њв”Ђв”Ђ dohoo_publisher.py              в†ђ YouTube via third-party (we replace with direct API)
в”њв”Ђв”Ђ config.py                       в†ђ Config
в””в”Ђв”Ђ main.py                         в†ђ Entry point

Documentations/                     в†ђ Test scenarios doc
```

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

## рџЋ¬ РќР°Р»Р°С€С‚СѓРІР°РЅРЅСЏ FFmpeg (РѕР±СЂРѕР±РєР° РІС–РґРµРѕ)

FFmpeg вЂ” С†Рµ РґРІРёР¶РѕРє РґР»СЏ РѕР±СЂРѕР±РєРё РІС–РґРµРѕ. РџСЂРѕРіСЂР°РјР° РІРёРєРѕСЂРёСЃС‚РѕРІСѓС” Р№РѕРіРѕ РґР»СЏ СѓРЅС–РєР°Р»С–Р·Р°С†С–С—: РґР·РµСЂРєР°Р»Рѕ, Р·РјС–РЅР° С€РІРёРґРєРѕСЃС‚С–, РєРѕСЂРµРєС†С–СЏ РєРѕР»СЊРѕСЂСѓ, РЅР°СЂС–Р·РєР° С‚РѕС‰Рѕ.

> **Р‘РµР· FFmpeg** РїСЂРѕРіСЂР°РјР° РЅРµ Р·РјРѕР¶Рµ РѕР±СЂРѕР±Р»СЏС‚Рё РІС–РґРµРѕ вЂ” РїСЂРё РЅР°С‚РёСЃРєР°РЅРЅС– "РџРћР§РђРўР РћР‘Р РћР‘РљРЈ" Р±СѓРґРµ РїРѕРјРёР»РєР°.

### РљСЂРѕРє 1: Р—Р°РІР°РЅС‚Р°Р¶РµРЅРЅСЏ

1. Р—Р°Р№РґС–С‚СЊ РЅР° https://www.gyan.dev/ffmpeg/builds/
2. Р’ СЂРѕР·РґС–Р»С– **release builds** СЃРєР°С‡Р°Р№С‚Рµ `ffmpeg-release-essentials.zip`
3. Р РѕР·РїР°РєСѓР№С‚Рµ Р°СЂС…С–РІ РІ Р·СЂСѓС‡РЅРµ РјС–СЃС†Рµ, РЅР°РїСЂРёРєР»Р°Рґ `C:\ffmpeg\`

РџС–СЃР»СЏ СЂРѕР·РїР°РєСѓРІР°РЅРЅСЏ СЃС‚СЂСѓРєС‚СѓСЂР° Р±СѓРґРµ:
```
C:\ffmpeg\
  в””в”Ђв”Ђ bin\
      в”њв”Ђв”Ђ ffmpeg.exe
      в”њв”Ђв”Ђ ffprobe.exe
      в””в”Ђв”Ђ ffplay.exe
```

### РљСЂРѕРє 2: Р”РѕРґР°С‚Рё РІ PATH

1. Win + R в†’ `sysdm.cpl` в†’ **Р”РѕРґР°С‚РєРѕРІРѕ** в†’ **Р—РјС–РЅРЅС– СЃРµСЂРµРґРѕРІРёС‰Р°**
2. Р’ **Path** (СЃРёСЃС‚РµРјРЅС– Р·РјС–РЅРЅС–) РґРѕРґР°Р№С‚Рµ `C:\ffmpeg\bin`
3. РќР°С‚РёСЃРЅС–С‚СЊ OK
4. **РџРµСЂРµР·Р°РїСѓСЃС‚С–С‚СЊ С‚РµСЂРјС–РЅР°Р»/IDE**

### РџРµСЂРµРІС–СЂРєР°

```
ffmpeg -version
ffprobe -version
```

РћР±РёРґРІС– РєРѕРјР°РЅРґРё РјР°СЋС‚СЊ РІРёРІРµСЃС‚Рё РІРµСЂСЃС–СЋ. РЇРєС‰Рѕ РїРёС€Рµ "not recognized" вЂ” PATH РЅРµ РЅР°Р»Р°С€С‚РѕРІР°РЅРёР№.

---

## рџ”— РќР°Р»Р°С€С‚СѓРІР°РЅРЅСЏ Ngrok (РїРµСЂРµРіР»СЏРґ РІС–РґРµРѕ Р· С‚РµР»РµС„РѕРЅСѓ)

Ngrok СЃС‚РІРѕСЂСЋС” РїСѓР±Р»С–С‡РЅРёР№ URL РґР»СЏ РІР°С€РѕРіРѕ Р»РѕРєР°Р»СЊРЅРѕРіРѕ СЃРµСЂРІРµСЂР°, С‰РѕР± РІРё РјРѕРіР»Рё РїРµСЂРµРіР»СЏРґР°С‚Рё РѕР±СЂРѕР±Р»РµРЅС– РІС–РґРµРѕ РїСЂСЏРјРѕ Р· С‚РµР»РµС„РѕРЅСѓ С‡РµСЂРµР· Telegram.

> **Р‘РµР· ngrok** РїСЂРѕРіСЂР°РјР° РїСЂР°С†СЋС” РїРѕРІРЅС–СЃС‚СЋ вЂ” РїСЂРѕСЃС‚Рѕ РєРЅРѕРїРєР° "Р”РР’РРўРРЎР¬ Р Р•Р—РЈР›Р¬РўРђРў" РЅРµ Р·'СЏРІРёС‚СЊСЃСЏ РІ Telegram, Р·Р°Р»РёС€РёС‚СЊСЃСЏ С‚С–Р»СЊРєРё "РЎРљРћРџР†Р®Р’РђРўР РџРћРЎРР›РђРќРќРЇ".

### РљСЂРѕРє 1: Р РµС”СЃС‚СЂР°С†С–СЏ

1. Р—Р°Р№РґС–С‚СЊ РЅР° https://ngrok.com/
2. РќР°С‚РёСЃРЅС–С‚СЊ **Sign up** (Р±РµР·РєРѕС€С‚РѕРІРЅРѕ)
3. РџС–РґС‚РІРµСЂРґС–С‚СЊ email

### РљСЂРѕРє 2: Р’СЃС‚Р°РЅРѕРІР»РµРЅРЅСЏ

1. Р—Р°Р№РґС–С‚СЊ РЅР° https://ngrok.com/download
2. РЎРєР°С‡Р°Р№С‚Рµ РІРµСЂСЃС–СЋ РґР»СЏ Windows
3. Р РѕР·РїР°РєСѓР№С‚Рµ `ngrok.exe`
4. Р”РѕРґР°Р№С‚Рµ РїР°РїРєСѓ Р· `ngrok.exe` РІ СЃРёСЃС‚РµРјРЅРёР№ **PATH**:
   - Win + R в†’ `sysdm.cpl` в†’ **Р”РѕРґР°С‚РєРѕРІРѕ** в†’ **Р—РјС–РЅРЅС– СЃРµСЂРµРґРѕРІРёС‰Р°**
   - Р’ **Path** РґРѕРґР°Р№С‚Рµ С€Р»СЏС… РґРѕ РїР°РїРєРё Р· `ngrok.exe`
   - РђР±Рѕ РїСЂРѕСЃС‚Рѕ РїРѕРєР»Р°РґС–С‚СЊ `ngrok.exe` РІ `C:\Windows\`

#### РџРµСЂРµРІС–СЂРєР°:
```
ngrok version
```

### РљСЂРѕРє 3: Authtoken

1. Р—Р°Р№РґС–С‚СЊ РЅР° https://dashboard.ngrok.com/get-started/your-authtoken
2. РЎРєРѕРїС–СЋР№С‚Рµ РІР°С€ С‚РѕРєРµРЅ
3. Р”РѕРґР°Р№С‚Рµ РІ `secrets.json`:

```json
{
  "Telegram": {
    "BotToken": "РІР°С€_Р±РѕС‚_С‚РѕРєРµРЅ",
    "NgrokAuthToken": "РІР°С€_ngrok_С‚РѕРєРµРЅ"
  }
}
```

### Р“РѕС‚РѕРІРѕ!

Р—Р°РїСѓСЃС‚С–С‚СЊ РїСЂРѕРіСЂР°РјСѓ вЂ” РІ Р»РѕРіР°С… РїРѕР±Р°С‡РёС‚Рµ:
```
[Ngrok] Tunnel active: https://xxxx-xx-xx.ngrok-free.app
```

Р’ Telegram РїС–Рґ РѕР±СЂРѕР±Р»РµРЅРёРј РІС–РґРµРѕ Р·'СЏРІРёС‚СЊСЃСЏ РєРЅРѕРїРєР° **в–¶пёЏ Р”РР’РРўРРЎР¬ Р Р•Р—РЈР›Р¬РўРђРў** вЂ” РЅР°С‚РёСЃРєР°С”С‚Рµ Р· С‚РµР»РµС„РѕРЅСѓ С– РґРёРІРёС‚РµСЃСЊ РІС–РґРµРѕ.

> вљ пёЏ РџСЂРё РїРµСЂС€РѕРјСѓ РІС–РґРєСЂРёС‚С‚С– ngrok РїРѕРєР°Р¶Рµ СЃС‚РѕСЂС–РЅРєСѓ "Visit Site" вЂ” РЅР°С‚РёСЃРЅС–С‚СЊ РєРЅРѕРїРєСѓ, РґР°Р»С– РІ С†С–Р№ СЃРµСЃС–С— Р±СЂР°СѓР·РµСЂР° РІР¶Рµ РЅРµ Р±СѓРґРµ РїРѕРєР°Р·СѓРІР°С‚Рё.

### Р’РёСЂС–С€РµРЅРЅСЏ РїСЂРѕР±Р»РµРј

РЇРєС‰Рѕ РїСЂРё Р·Р°РїСѓСЃРєСѓ РїСЂРѕРіСЂР°РјРё ngrok РЅРµ СЃС‚Р°СЂС‚СѓС” Р°Р±Рѕ РІ Р»РѕРіР°С… РїРѕРјРёР»РєР° вЂ” РјРѕР¶Р»РёРІРѕ РїРѕРїРµСЂРµРґРЅС–Р№ РїСЂРѕС†РµСЃ ngrok Р·Р°Р»РёС€РёРІСЃСЏ РІРёСЃС–С‚Рё. Р’РёРєРѕРЅР°Р№С‚Рµ РІ С‚РµСЂРјС–РЅР°Р»С–:

```
taskkill /f /im ngrok.exe
```

РџС–СЃР»СЏ С†СЊРѕРіРѕ РїРµСЂРµР·Р°РїСѓСЃС‚С–С‚СЊ РїСЂРѕРіСЂР°РјСѓ.

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
    "BaseUrl": "http://localhost:5000",
    "MaxConcurrentJobs": 1
  },
  "Publishing": {
    "TimeZoneId": "Europe/Kiev",
    "DailyPublishTime": "10:00",
    "YouTubeChannels": ["Default"]
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
    "ClientId": "<YOUR_CLIENT_ID>",
    "ClientSecret": "<YOUR_CLIENT_SECRET>",
    "RefreshToken": "<YOUR_REFRESH_TOKEN>"
  },
  "GoogleSheets": {
    "SpreadsheetId": "REAL_SPREADSHEET_ID"
  }
}
```

.NET merges both at startup вЂ” `secrets.json` overrides `appsettings.json` for matching keys.

**вљ пёЏ YouTube/GoogleSheets config is split:** non-secret defaults live in `appsettings.json`, credentials/ids live in `secrets.json`.

---

## Authentication Summary

| Service | Auth Method | Credentials |
|---------|------------|-------------|
| Google Drive | Service Account | `secrets.json` в†’ `GoogleDrive.ServiceAccount` block |
| Google Sheets | Service Account (same) | Same service account. Share the spreadsheet with `client_email` as Editor |
| YouTube upload | **OAuth2 (NOT service account!)** | `secrets.json` в†’ `YouTube.ClientId` + `ClientSecret` + `RefreshToken` |
| Telegram | Bot token | `secrets.json` в†’ `Telegram.BotToken` |

**CRITICAL: YouTube Data API does NOT support Service Account auth for uploading videos. Only OAuth2 with a real user's refresh token works. Do NOT try to use the service account JSON for YouTube.**

---

## Р†РЅС‚РµРіСЂР°С†С–СЏ Google Drive

### рџ“Њ РћРіР»СЏРґ РђСЂС…С–С‚РµРєС‚СѓСЂРё (Overview)
Р¦РµР№ РјРѕРґСѓР»СЊ РІС–РґРїРѕРІС–РґР°С” Р·Р° Р°РІС‚РѕРјР°С‚РёС‡РЅРёР№ РјРѕРЅС–С‚РѕСЂРёРЅРі (polling) СЃРїРµС†РёС„С–С‡РЅРѕС— РїР°РїРєРё Google Drive. Р’С–РЅ Р·РЅР°С…РѕРґРёС‚СЊ РЅРѕРІС– РІС–РґРµРѕС„Р°Р№Р»Рё, Р·Р°РІР°РЅС‚Р°Р¶СѓС” С—С… Р»РѕРєР°Р»СЊРЅРѕ С– РІРµРґРµ СЂРµС”СЃС‚СЂ РІР¶Рµ Р·Р°РІР°РЅС‚Р°Р¶РµРЅРёС… С„Р°Р№Р»С–РІ, С‰РѕР± СѓРЅРёРєРЅСѓС‚Рё С—С… РїРѕРІС‚РѕСЂРЅРѕРіРѕ РІРёРєРѕРЅР°РЅРЅСЏ.

РЎРёСЃС‚РµРјР° СЃРєР»Р°РґР°С”С‚СЊСЃСЏ Р· С‚СЂСЊРѕС… РєР»СЋС‡РѕРІРёС… РєРѕРјРїРѕРЅРµРЅС‚С–РІ:
1. **`GoogleDriveWatcher`** (РЎРµСЂРІС–СЃ РїС–РґРєР»СЋС‡РµРЅРЅСЏ): Р’СЃС‚Р°РЅРѕРІР»СЋС” Р°СѓС‚РµРЅС‚РёС„С–РєР°С†С–СЋ С‡РµСЂРµР· Service Account, С„РѕСЂРјСѓС” РїРѕС€СѓРєРѕРІРёР№ Р·Р°РїРёС‚ РґРѕ Google Drive API (С€СѓРєР°С” РІРёРєР»СЋС‡РЅРѕ `mimeType contains 'video/'`) С‚Р° СЃС‚СЏРіСѓС” С„Р°Р№Р»Рё С‡Р°РЅРєР°РјРё.
2. **`KnownFilesStore`** (РњРµРЅРµРґР¶РµСЂ СЃС‚Р°РЅСѓ): Р§РёС‚Р°С” С‚Р° РѕРЅРѕРІР»СЋС” Р»РѕРєР°Р»СЊРЅРёР№ СЃР»РѕРІРЅРёРє `known_files.json`. Р’С–РЅ РіР°СЂР°РЅС‚СѓС” С–РґРµРјРїРѕС‚РµРЅС‚РЅС–СЃС‚СЊ СЃРёСЃС‚РµРјРё (РѕРґРЅРµ РІС–РґРµРѕ - РѕРґРЅРµ Р·Р°РІР°РЅС‚Р°Р¶РµРЅРЅСЏ), Р° С‚Р°РєРѕР¶ РІРјС–С” РіСЂР°С†С–РѕР·РЅРѕ РѕР±СЂРѕР±Р»СЏС‚Рё Р±РёС‚С– Р°Р±Рѕ РїРѕСЂРѕР¶РЅС– JSON-СЃС‚Р°РЅРё.
3. **`Worker`** (Р¤РѕРЅРѕРІРёР№ РїСЂРѕС†РµСЃ): РќРµСЃРєС–РЅС‡РµРЅРЅРёР№ С†РёРєР», С‰Рѕ Р¶РёРІРµ РІ РїСЂРѕС”РєС‚С– `TubePilot.Worker`. Р’С–РЅ РїСЂРѕРєРёРґР°С”С‚СЊСЃСЏ РєРѕР¶РЅС– 30 СЃРµРєСѓРЅРґ С–, Р·Р°РІРґСЏРєРё РїР°С‚РµСЂРЅСѓ `IOptionsMonitor`, РІРјС–С” "РЅР° Р»СЊРѕС‚Сѓ" РїС–РґС…РѕРїР»СЋРІР°С‚Рё Р·РјС–РЅРё РїР°РїРѕРє С–Р· РєРѕРЅС„С–РіС–РІ Р±РµР· РїРµСЂРµР·Р°РїСѓСЃРєСѓ РїСЂРѕРіСЂР°РјРё.

*рџ’Ў Р¤С–С‡Р° РґР»СЏ DevOps: Р’Р»Р°СЃС‚РёРІС–СЃС‚СЊ `FolderId` РјРѕР¶РЅР° Р·РјС–РЅСЋРІР°С‚Рё РїСЂСЏРјРѕ "РЅР° РіР°СЂСЏС‡Сѓ"! РџСЂРѕСЃС‚Рѕ РїРµСЂРµРїРёС€С–С‚СЊ ID Сѓ С„Р°Р№Р»С–, РЅР°С‚РёСЃРЅС–С‚СЊ "Р—Р±РµСЂРµРіС‚Рё", С– `Worker` РїС–РґС…РѕРїРёС‚СЊ РЅРѕРІСѓ РїР°РїРєСѓ РїС–Рґ С‡Р°СЃ РЅР°СЃС‚СѓРїРЅРѕРіРѕ С†РёРєР»Сѓ.*

---

## YouTube Uploader

This module implements `TubePilot.Core.Contracts.IYouTubeUploader` using:
- OAuth2 refresh token (`YouTube:ClientId`, `YouTube:ClientSecret`, `YouTube:RefreshToken`)
- resumable uploads (chunked `PUT` with `Content-Range`)
- optional thumbnail upload
- visibility selection (`public`/`unlisted`/`private`) for immediate uploads
- scheduled publishing (`publishAt` + `privacyStatus=private` в†’ becomes public at publish time)

### Manual smoke test (code snippet)

```csharp
var result = await youTubeUploader.UploadAsync(
    new YouTubeUploadRequest(
        VideoFilePath: @"C:\videos\test.mp4",
        Title: "TubePilot test",
        Description: "Uploaded by TubePilot",
        Tags: ["test", "tubepilot"],
        Visibility: YouTubeVideoVisibility.Unlisted,
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

---

## FFmpeg Processing Details

### Filters

| Filter | FFmpeg command | Notes |
|--------|---------------|-------|
| Mirror (hflip) | `-vf hflip -c:a copy` | Simple, no re-encode of audio |
| Volume -10-20% | `-af volume=0.85 -c:v copy` | Random 0.80-0.90, video stream copy |
| Slow down 4-7% | `-filter_complex "[0:v]setpts=1.05*PTS[v];[0:a]atempo=0.952[a]" -map [v] -map [a]` | Random factor 1.04-1.07 |
| Speed up 3-5% | `-filter_complex "[0:v]setpts=0.97*PTS[v];[0:a]atempo=1.03[a]" -map [v] -map [a]` | Random factor 1.03-1.05 |
| Color correction | `-vf "eq=saturation=1.05:brightness=-0.05:gamma=0.98" -c:a copy` | Random ranges per param |
| Slice 2:30-3:10 | `-ss {start} -t {duration} -c copy` | Random duration 150-190s per segment |
| Slice 5:10-7:10 | `-ss {start} -t {duration} -c copy` | Random duration 310-430s per segment |
| QR overlay | `-i qr.png -filter_complex "[1:v]scale=iw*0.25:-1[qr];[0:v][qr]overlay=..."` | First 10 seconds only |
| Rotate 3-5В° | `-vf "scale=iw*1.12:ih*1.12,rotate=0.07:fillcolor=black,crop=..."` | Zoom to hide black bars |
| Downscale 1080p | `-vf "scale=-2:1080"` | Only if source > 1080p |

### Combined single-pass processing

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

---

## Telegram Publishing Flow

After video processing is complete:

```
[After processing completes, for each result video/segment:]

Bot sends: thumbnail preview + video info (duration, size)
  Button: [рџ“¤ Publish to YouTube]

User taps [Publish] в†’
  Bot: "рџ“є Select channel:"
  Button: [247AI Test Channel]  (from API: channels.list mine=true)

User selects channel в†’
  Bot: "вњЏпёЏ Enter video title:"

User types title в†’
  Bot: "рџ“ќ Enter description: (/skip to skip)"

User types description в†’
  Bot: "рџЏ· Enter tags (comma separated, /skip to skip):"

User types tags в†’
  Bot: "рџ“… When to publish?"
  Buttons: [Now] [Next free slot] [Pick date]

  If [Pick date]:
    Bot: "Enter date (YYYY-MM-DD HH:MM):"

User selects schedule в†’
  Bot: "рџЊђ Select visibility:"
  Buttons: [Public] [Unlisted] [Private] [Skip (Public)]

User selects visibility в†’
  Bot: "рџ“‹ Confirm upload" (summary)
  Buttons: [вњ… Confirm] [вќЊ Cancel]

User confirms в†’
  Bot: "вЏі Uploading to YouTube..."
  [resumable upload with progress %]
  Bot: "вњ… Published! https://youtube.com/watch?v={id}"
```

For segments (after cut):
```
Button: [рџ“¤ Publish ALL segments]

If tapped:
  Bot: "вњЏпёЏ Enter title template (use {N} for part number):"
  User: "Beach Carnival Part {N}"
  в†’ Auto-increments schedule: Part 1 = today, Part 2 = tomorrow, etc.
```

### FSM States

```csharp
public enum PublishWizardStep
{
    Idle,
    WaitingForChannel,
    WaitingForTitle,
    WaitingForDescription,
    WaitingForTags,
    WaitingForScheduleChoice,
    WaitingForCustomDate,
    WaitingForVisibility,
    Confirm,
    Uploading
}
```

---

## Google Sheets Audit Logging

Logs every published video to Google Sheets for transparency.

**Authentication:** Service Account JSON (the same key used for Drive).

**Columns:**
| ts_utc | channel | source_file | title | youtube_id | youtube_url | status | scheduled_at_utc | quota_used | notes |
|--------|---------|-------------|-------|------------|-------------|--------|-----------------|------------|-------|

**When to log:**
- After successful upload: status = `published` or `scheduled`
- After failed upload: status = `failed`, include error in notes

---

## Scheduling Logic

**Rules:**
- Max 1 upload/day per channel (configurable)
- "Next free slot" = check last scheduled date for this channel, add 1 day
- Time = configured daily posting time (e.g., 10:00 in channel timezone)
- "Publish all segments" = auto-increment: Part 1 today, Part 2 tomorrow, etc.
- Track scheduled dates in in-memory dict per chat+channel

---

## Key Technical Constraints

1. **Resumable upload is mandatory** for YouTube вЂ” standard upload fails on files >100MB. Always use `uploadType=resumable` with 4MB chunks.
2. **Access token lives ~1 hour** вЂ” always check expiry before upload, refresh from refresh_token if expired.
3. **YouTube quota: 10,000 units/day/project** вЂ” upload = 1600 units в‰€ 6 uploads/day. Track usage.
4. **scheduledPublish requires `privacyStatus: "private"`** вЂ” otherwise YouTube ignores `publishAt`.
5. **Cut is stream-copy** (`-c copy`, fast, no quality loss). Any filter (mirror, color, speed) forces re-encode (`libx264 + aac`).
6. **Combined single-pass** вЂ” when multiple filters selected, chain all into one `-filter_complex`. One re-encode, not multiple passes.
7. **Service Account for Drive/Sheets, OAuth2 for YouTube** вЂ” do not mix them up.

---

## Reference Code

The `video-bot-project/` folder contains a working Python implementation by the previous team. Use it as reference for:

- **FFmpeg commands:** `video-bot-project/video_processor.py` вЂ” all 10 filters with exact FFmpeg CLI args, single-pass combined processing, progress reporting. Port this logic to C# subprocess calls.
- **Telegram UX:** `video-bot-project/bot.py` вЂ” publishing flow (channel selection в†’ title в†’ description в†’ upload), result messages with thumbnails, progress updates.
- **YouTube publishing:** `video-bot-project/dohoo_publisher.py` вЂ” uses third-party API (we replace with direct YouTube API), but the flow/UX is the same.

---

## QA & Test Scenarios

### Automated Tests

```bash
# All tests
dotnet test TubePilot.sln

# Without integration tests (require ffmpeg in PATH)
dotnet test --filter "FullyQualifiedName!~IntegrationTests"
```

### рџџў Scenario 1: Telegram Bot Auto-Discovery (РђРІС‚РѕСЂРёР·Р°С†С–СЏ)
1. Р—Р°РїСѓСЃС‚РёС‚Рё РїСЂРѕРµРєС‚ `TubePilot.Worker` (`dotnet run`).
2. Р’С–РґРєСЂРёС‚Рё Telegram, Р·РЅР°Р№С‚Рё СЃРІРѕРіРѕ Р±РѕС‚Р° С– РІС–РґРїСЂР°РІРёС‚Рё Р№РѕРјСѓ РєРѕРјР°РЅРґСѓ `/start`.
3. **Expected:** Р‘РѕС‚ РІС–РґРїРѕРІС–РґР°С”: `вњ… РђРІС‚РѕСЂРёР·Р°С†С–СЏ СѓСЃРїС–С€РЅР°! РўРµРїРµСЂ СЏ Р±СѓРґСѓ РЅР°РґСЃРёР»Р°С‚Рё СЃСЋРґРё С–РЅС‚РµСЂС„РµР№СЃ...`.
4. **Expected:** РЈ РєРѕРЅСЃРѕР»С– РІРѕСЂРєРµСЂР°: `Successfully linked bot to user ChatId: [Р’РђРЁ_ID]`.
5. **Expected:** Р’ РєРѕСЂРµРЅС– `TubePilot.Worker` СЃС‚РІРѕСЂСЋС”С‚СЊСЃСЏ С„Р°Р№Р» `telegram_subscriber.txt` Р· РІР°С€РёРј ID.

### рџџў Scenario 2: Google Drive Auto-Download (Р—Р°РІР°РЅС‚Р°Р¶РµРЅРЅСЏ)
1. Р’РѕСЂРєРµСЂ Р·Р°РїСѓС‰РµРЅРёР№ С– РѕС‡С–РєСѓС”.
2. Р—Р°Р№С‚Рё РІ Google Drive Сѓ РїР°РїРєСѓ `FolderId`.
3. Р—Р°РІР°РЅС‚Р°Р¶РёС‚Рё С‚СѓРґРё С‚РµСЃС‚РѕРІРµ РІС–РґРµРѕ (`.mp4`).
4. **Expected:** РџСЂРѕС‚СЏРіРѕРј 30 СЃРµРєСѓРЅРґ Сѓ РєРѕРЅСЃРѕР»С–: `Downloading file ...`.
5. **Expected:** `Successfully processed test_short.mp4...`.
6. **Expected:** Р’С–РґРµРѕ Р·'СЏРІР»СЏС”С‚СЊСЃСЏ Сѓ РїР°РїС†С– `Downloads`.

### рџџў Scenario 3: Telegram Notification & UI Render (РЎРїРѕРІС–С‰РµРЅРЅСЏ)
1. РЎС†РµРЅР°СЂС–Р№ 2 СѓСЃРїС–С€РЅРѕ Р·Р°РІРµСЂС€РµРЅРѕ.
2. **Expected:** Telegram РѕС‚СЂРёРјСѓС” РїРѕРІС–РґРѕРјР»РµРЅРЅСЏ РІС–Рґ Р±РѕС‚Р° Р· РЅР°Р·РІРѕСЋ С„Р°Р№Р»Сѓ С‚Р° РІР°РіРѕСЋ (РІ РњР‘).
3. **Expected:** Inline-РєР»Р°РІС–Р°С‚СѓСЂР° Р· 10 РѕРїС†С–СЏРјРё СѓРЅС–РєР°Р»С–Р·Р°С†С–С— + СЃРёСЃС‚РµРјРЅС– РєРЅРѕРїРєРё ("Р’РёР±СЂР°С‚Рё РІСЃС–", "РћС‡РёСЃС‚РёС‚Рё", "РџРћР§РђРўР РћР‘Р РћР‘РљРЈ").

### рџџў Scenario 4: Interactive Keyboard (Р РµР°РєС‚РёРІРЅС–СЃС‚СЊ UI/UX)
1. РќР°С‚РёСЃРЅСѓС‚Рё "рџЄћ Р”Р·РµСЂРєР°Р»Рѕ (HFlip)" в†’ СЃРёРјРІРѕР» Р·РјС–РЅСЋС”С‚СЊСЃСЏ Р· `рџ”` РЅР° `вњ…`.
2. РќР°С‚РёСЃРЅСѓС‚Рё "рџ’  Р’РёР±СЂР°С‚Рё РІСЃС–" в†’ РІСЃС– 10 РєРЅРѕРїРѕРє СЃС‚Р°СЋС‚СЊ `вњ…`.
3. РќР°С‚РёСЃРЅСѓС‚Рё "вњ–пёЏ РћС‡РёСЃС‚РёС‚Рё" в†’ РІСЃС– РєРЅРѕРїРєРё РЅР°Р·Р°Рґ РЅР° `рџ”`.

### рџџў Scenario 5: FFmpeg Processing & Progress Bar
1. Р’РёР±СЂР°С‚Рё С„С–Р»СЊС‚СЂ (РЅР°РїСЂ., `rotate`).
2. РќР°С‚РёСЃРЅСѓС‚Рё "в–¶пёЏ РџРћР§РђРўР РћР‘Р РћР‘РљРЈ".
3. **Expected:** РљР»Р°РІС–Р°С‚СѓСЂР° Р·РЅРёРєР°С”, Р·'СЏРІР»СЏС”С‚СЊСЃСЏ `вљ™пёЏ GPU РћР‘Р РћР‘РљРђ: РђРљРўРР’РќРћ`.
4. **Expected:** Progress bar РѕРЅРѕРІР»СЋС”С‚СЊСЃСЏ РІС–Рґ `[----------] 0%` РґРѕ `[##########] 100%`.
5. **Expected:** `вњ… РЈРќР†РљРђР›Р†Р—РђР¦Р†Р® Р—РђР’Р•Р РЁР•РќРћ` + РєС–Р»СЊРєС–СЃС‚СЊ С„С–Р»СЊС‚СЂС–РІ.

### рџџў Scenario 6: Video Delivery & Mini-Server (Р”РѕСЃС‚Р°РІРєР° СЂРµР·СѓР»СЊС‚Р°С‚Сѓ)
1. РЎС†РµРЅР°СЂС–Р№ 5 Р·Р°РІРµСЂС€РµРЅРѕ.
2. **Expected:** Р‘РѕС‚ РЅР°РґСЃРёР»Р°С” result card Р· С–РЅС„Рѕ РїСЂРѕ РІС–РґРµРѕ.
3. **Expected:** РљРЅРѕРїРєР° "в–¶пёЏ Р”РёРІРёС‚РёСЃСЊ" (СЏРєС‰Рѕ ngrok Р°РєС‚РёРІРЅРёР№).
4. РќР°С‚РёСЃРЅСѓС‚Рё в†’ РІС–РґРєСЂРёС”С‚СЊСЃСЏ Р±СЂР°СѓР·РµСЂ `http://localhost:5000/play/...` Р· РІС–РґРµРѕРїР»РµС”СЂРѕРј.

### рџџЎ Scenario 7: Idempotency (РўРµСЃС‚ РґСѓР±Р»С–РєР°С‚С–РІ)
1. Р—Р°Р»РёС€РёС‚Рё Р·Р°РІР°РЅС‚Р°Р¶РµРЅРµ РІС–РґРµРѕ РІ Google Drive. РџРµСЂРµР·Р°РїСѓСЃС‚РёС‚Рё РїСЂРѕРіСЂР°РјСѓ.
2. **Expected:** Р’РѕСЂРєРµСЂ РїРѕР±Р°С‡РёС‚СЊ РІС–РґРµРѕ, Р·РІС–СЂРёС‚СЊ Р· `known_files.json` С– **РїСЂРѕРїСѓСЃС‚РёС‚СЊ** РїРѕРІС‚РѕСЂРЅРµ Р·Р°РІР°РЅС‚Р°Р¶РµРЅРЅСЏ.

### рџџЎ Scenario 8: Р’С–РґСЃСѓС‚РЅС–СЃС‚СЊ Р°Р±Рѕ РїРѕРјРёР»РєРѕРІРёР№ `FolderId`
1. РЎС‚РµСЂС‚Рё `"FolderId"` РІ `secrets.json`. Р—Р°РїСѓСЃС‚РёС‚Рё РїСЂРѕРіСЂР°РјСѓ.
2. **Expected:** Worker **РЅРµ РІРїР°РґРµ**. Р’РёРІРµРґРµ РїРѕРїРµСЂРµРґР¶РµРЅРЅСЏ: `GoogleDrive:FolderId is missing...` С– Р·Р°СЃРЅРµ Сѓ С†РёРєР»С–-РѕС‡С–РєСѓРІР°РЅРЅС–.

### рџ”ґ Scenario 9: Missing Service Account (Fail-fast)
1. Р’РёРґР°Р»РёС‚Рё Р±Р»РѕРє `"ServiceAccount"` Р· `secrets.json`.
2. **Expected:** РџСЂРѕРіСЂР°РјР° **РІРїР°РґРµ** РїСЂРё СЃС‚Р°СЂС‚С– Р· `InvalidOperationException: ServiceAccount configuration is missing.`.

### рџџў Scenario 10: Corrupted State (РџРѕС€РєРѕРґР¶РµРЅРёР№ РєРµС€ JSON)
1. Р’С–РґРєСЂРёС‚Рё `known_files.json`, РІРёРґР°Р»РёС‚Рё РІРµСЃСЊ С‚РµРєСЃС‚ Р°Р±Рѕ РЅР°РїРёСЃР°С‚Рё РЅРµРІР°Р»С–РґРЅРёР№ JSON.
2. **Expected:** РџСЂРѕРіСЂР°РјР° **РЅРµ РІРїР°РґРµ**. РќР°РїРёС€Рµ `Failed to load known files` РІ Р»РѕРі, РѕР±РЅСѓР»РёС‚СЊ СЃРїРёСЃРѕРє С– РїРѕС‡РЅРµ Р· С‡РёСЃС‚РѕРіРѕ Р°СЂРєСѓС€Р°.

---

## Files You Need To Provide

```
вњ… client_secrets.json            в†’ YouTube section in secrets.json (ClientId + ClientSecret)
вњ… service_account.json           в†’ Already in secrets.json under GoogleDrive.ServiceAccount
вњ… REFRESH_TOKEN                  в†’ YouTube.RefreshToken in secrets.json
вњ… TELEGRAM_BOT_TOKEN             в†’ Already in secrets.json
вњ… GDRIVE_FOLDER_ID               в†’ Already in secrets.json
вњ… GSHEETS_SPREADSHEET_ID         в†’ GoogleSheets.SpreadsheetId in secrets.json
```

All credentials go into ONE file: `secrets.json`. No separate .env needed.

