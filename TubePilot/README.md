# TubePilot

Система автопостингу відео на YouTube, керована через Telegram-бота. Моніторить Google Drive → обробляє відео через FFmpeg → публікує на YouTube → логує в Google Sheets.

---

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
│   │   ├── IVideoProcessor.cs     ← ✅ Done
│   │   └── IYouTubeUploader.cs    ← ✅ Done
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
│   │   ├── TelegramBotService.cs   ← ✅ Done (UI + publishing wizard)
│   │   ├── TelegramUploadJobRunner.cs ← ✅ Done (YouTube upload orchestration)
│   │   ├── Models/                 ← ✅ Done
│   │   └── Options/                ← ✅ Done
│   ├── Video/
│   │   └── FfmpegVideoProcessor.cs ← ✅ Done (real FFmpeg processing)
│   ├── YouTube/
│   │   ├── YouTubeUploader.cs      ← ✅ Done (OAuth2 + resumable upload)
│   │   └── OAuthRefreshTokenAccessTokenProvider.cs ← ✅ Done
│   └── GoogleSheets/
│       └── GoogleSheetsLogger.cs   ← ✅ Done
├── TubePilot.Worker/               ← Entry point
│   ├── Program.cs                  ← ✅ Done
│   ├── Worker.cs                   ← ✅ Done (Drive polling loop)
│   ├── appsettings.json            ← Config (general, committed to git)
│   └── secrets.json                ← Config (secrets, NOT in git)
├── TubePilot.Infrastructure.Tests/ ← Unit & integration tests
├── TubePilot.Worker.Tests/         ← Endpoint tests
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

## 🎬 Налаштування FFmpeg (обробка відео)

FFmpeg — це движок для обробки відео. Програма використовує його для унікалізації: дзеркало, зміна швидкості, корекція кольору, нарізка тощо.

> **Без FFmpeg** програма не зможе обробляти відео — при натисканні "ПОЧАТИ ОБРОБКУ" буде помилка.

### Крок 1: Завантаження

1. Зайдіть на https://www.gyan.dev/ffmpeg/builds/
2. В розділі **release builds** скачайте `ffmpeg-release-essentials.zip`
3. Розпакуйте архів в зручне місце, наприклад `C:\ffmpeg\`

Після розпакування структура буде:
```
C:\ffmpeg\
  └── bin\
      ├── ffmpeg.exe
      ├── ffprobe.exe
      └── ffplay.exe
```

### Крок 2: Додати в PATH

1. Win + R → `sysdm.cpl` → **Додатково** → **Змінні середовища**
2. В **Path** (системні змінні) додайте `C:\ffmpeg\bin`
3. Натисніть OK
4. **Перезапустіть термінал/IDE**

### Перевірка

```
ffmpeg -version
ffprobe -version
```

Обидві команди мають вивести версію. Якщо пише "not recognized" — PATH не налаштований.

---

## 🔗 Налаштування Ngrok (перегляд відео з телефону)

Ngrok створює публічний URL для вашого локального сервера, щоб ви могли переглядати оброблені відео прямо з телефону через Telegram.

> **Без ngrok** програма працює повністю — просто кнопка "ДИВИТИСЬ РЕЗУЛЬТАТ" не з'явиться в Telegram, залишиться тільки "СКОПІЮВАТИ ПОСИЛАННЯ".

### Крок 1: Реєстрація

1. Зайдіть на https://ngrok.com/
2. Натисніть **Sign up** (безкоштовно)
3. Підтвердіть email

### Крок 2: Встановлення

1. Зайдіть на https://ngrok.com/download
2. Скачайте версію для Windows
3. Розпакуйте `ngrok.exe`
4. Додайте папку з `ngrok.exe` в системний **PATH**:
   - Win + R → `sysdm.cpl` → **Додатково** → **Змінні середовища**
   - В **Path** додайте шлях до папки з `ngrok.exe`
   - Або просто покладіть `ngrok.exe` в `C:\Windows\`

#### Перевірка:
```
ngrok version
```

### Крок 3: Authtoken

1. Зайдіть на https://dashboard.ngrok.com/get-started/your-authtoken
2. Скопіюйте ваш токен
3. Додайте в `secrets.json`:

```json
{
  "Telegram": {
    "BotToken": "ваш_бот_токен",
    "NgrokAuthToken": "ваш_ngrok_токен"
  }
}
```

### Готово!

Запустіть програму — в логах побачите:
```
[Ngrok] Tunnel active: https://xxxx-xx-xx.ngrok-free.app
```

В Telegram під обробленим відео з'явиться кнопка **▶️ ДИВИТИСЬ РЕЗУЛЬТАТ** — натискаєте з телефону і дивитесь відео.

> ⚠️ При першому відкритті ngrok покаже сторінку "Visit Site" — натисніть кнопку, далі в цій сесії браузера вже не буде показувати.

### Вирішення проблем

Якщо при запуску програми ngrok не стартує або в логах помилка — можливо попередній процес ngrok залишився висіти. Виконайте в терміналі:

```
taskkill /f /im ngrok.exe
```

Після цього перезапустіть програму.

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

## Інтеграція Google Drive

### 📌 Огляд Архітектури (Overview)
Цей модуль відповідає за автоматичний моніторинг (polling) специфічної папки Google Drive. Він знаходить нові відеофайли, завантажує їх локально і веде реєстр вже завантажених файлів, щоб уникнути їх повторного виконання.

Система складається з трьох ключових компонентів:
1. **`GoogleDriveWatcher`** (Сервіс підключення): Встановлює аутентифікацію через Service Account, формує пошуковий запит до Google Drive API (шукає виключно `mimeType contains 'video/'`) та стягує файли чанками.
2. **`KnownFilesStore`** (Менеджер стану): Читає та оновлює локальний словник `known_files.json`. Він гарантує ідемпотентність системи (одне відео - одне завантаження), а також вміє граціозно обробляти биті або порожні JSON-стани.
3. **`Worker`** (Фоновий процес): Нескінченний цикл, що живе в проєкті `TubePilot.Worker`. Він прокидається кожні 30 секунд і, завдяки патерну `IOptionsMonitor`, вміє "на льоту" підхоплювати зміни папок із конфігів без перезапуску програми.

*💡 Фіча для DevOps: Властивість `FolderId` можна змінювати прямо "на гарячу"! Просто перепишіть ID у файлі, натисніть "Зберегти", і `Worker` підхопить нову папку під час наступного циклу.*

---

## YouTube Uploader

This module implements `TubePilot.Core.Contracts.IYouTubeUploader` using:
- OAuth2 refresh token (`YouTube:ClientId`, `YouTube:ClientSecret`, `YouTube:RefreshToken`)
- resumable uploads (chunked `PUT` with `Content-Range`)
- optional thumbnail upload
- visibility selection (`public`/`unlisted`/`private`) for immediate uploads
- scheduled publishing (`publishAt` + `privacyStatus=private` → becomes public at publish time)

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
| Rotate 3-5° | `-vf "scale=iw*1.12:ih*1.12,rotate=0.07:fillcolor=black,crop=..."` | Zoom to hide black bars |
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
  Bot: "🌐 Select visibility:"
  Buttons: [Public] [Unlisted] [Private] [Skip (Public)]

User selects visibility →
  Bot: "📋 Confirm upload" (summary)
  Buttons: [✅ Confirm] [❌ Cancel]

User confirms →
  Bot: "⏳ Uploading to YouTube..."
  [resumable upload with progress %]
  Bot: "✅ Published! https://youtube.com/watch?v={id}"
```

For segments (after cut):
```
Button: [📤 Publish ALL segments]

If tapped:
  Bot: "✏️ Enter title template (use {N} for part number):"
  User: "Beach Carnival Part {N}"
  → Auto-increments schedule: Part 1 = today, Part 2 = tomorrow, etc.
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

1. **Resumable upload is mandatory** for YouTube — standard upload fails on files >100MB. Always use `uploadType=resumable` with 4MB chunks.
2. **Access token lives ~1 hour** — always check expiry before upload, refresh from refresh_token if expired.
3. **YouTube quota: 10,000 units/day/project** — upload = 1600 units ≈ 6 uploads/day. Track usage.
4. **scheduledPublish requires `privacyStatus: "private"`** — otherwise YouTube ignores `publishAt`.
5. **Cut is stream-copy** (`-c copy`, fast, no quality loss). Any filter (mirror, color, speed) forces re-encode (`libx264 + aac`).
6. **Combined single-pass** — when multiple filters selected, chain all into one `-filter_complex`. One re-encode, not multiple passes.
7. **Service Account for Drive/Sheets, OAuth2 for YouTube** — do not mix them up.

---

## Reference Code

The `video-bot-project/` folder contains a working Python implementation by the previous team. Use it as reference for:

- **FFmpeg commands:** `video-bot-project/video_processor.py` — all 10 filters with exact FFmpeg CLI args, single-pass combined processing, progress reporting. Port this logic to C# subprocess calls.
- **Telegram UX:** `video-bot-project/bot.py` — publishing flow (channel selection → title → description → upload), result messages with thumbnails, progress updates.
- **YouTube publishing:** `video-bot-project/dohoo_publisher.py` — uses third-party API (we replace with direct YouTube API), but the flow/UX is the same.

---

## QA & Test Scenarios

### Automated Tests

```bash
# All tests
dotnet test TubePilot.sln

# Without integration tests (require ffmpeg in PATH)
dotnet test --filter "FullyQualifiedName!~IntegrationTests"
```

### 🟢 Scenario 1: Telegram Bot Auto-Discovery (Авторизація)
1. Запустити проект `TubePilot.Worker` (`dotnet run`).
2. Відкрити Telegram, знайти свого бота і відправити йому команду `/start`.
3. **Expected:** Бот відповідає: `✅ Авторизація успішна! Тепер я буду надсилати сюди інтерфейс...`.
4. **Expected:** У консолі воркера: `Successfully linked bot to user ChatId: [ВАШ_ID]`.
5. **Expected:** В корені `TubePilot.Worker` створюється файл `telegram_subscriber.txt` з вашим ID.

### 🟢 Scenario 2: Google Drive Auto-Download (Завантаження)
1. Воркер запущений і очікує.
2. Зайти в Google Drive у папку `FolderId`.
3. Завантажити туди тестове відео (`.mp4`).
4. **Expected:** Протягом 30 секунд у консолі: `Downloading file ...`.
5. **Expected:** `Successfully processed test_short.mp4...`.
6. **Expected:** Відео з'являється у папці `Downloads`.

### 🟢 Scenario 3: Telegram Notification & UI Render (Сповіщення)
1. Сценарій 2 успішно завершено.
2. **Expected:** Telegram отримує повідомлення від бота з назвою файлу та вагою (в МБ).
3. **Expected:** Inline-клавіатура з 10 опціями унікалізації + системні кнопки ("Вибрати всі", "Очистити", "ПОЧАТИ ОБРОБКУ").

### 🟢 Scenario 4: Interactive Keyboard (Реактивність UI/UX)
1. Натиснути "🪞 Дзеркало (HFlip)" → символ змінюється з `🔘` на `✅`.
2. Натиснути "💠 Вибрати всі" → всі 10 кнопок стають `✅`.
3. Натиснути "✖️ Очистити" → всі кнопки назад на `🔘`.

### 🟢 Scenario 5: FFmpeg Processing & Progress Bar
1. Вибрати фільтр (напр., `rotate`).
2. Натиснути "▶️ ПОЧАТИ ОБРОБКУ".
3. **Expected:** Клавіатура зникає, з'являється `⚙️ GPU ОБРОБКА: АКТИВНО`.
4. **Expected:** Progress bar оновлюється від `[----------] 0%` до `[##########] 100%`.
5. **Expected:** `✅ УНІКАЛІЗАЦІЮ ЗАВЕРШЕНО` + кількість фільтрів.

### 🟢 Scenario 6: Video Delivery & Mini-Server (Доставка результату)
1. Сценарій 5 завершено.
2. **Expected:** Бот надсилає result card з інфо про відео.
3. **Expected:** Кнопка "▶️ Дивитись" (якщо ngrok активний).
4. Натиснути → відкриється браузер `http://localhost:5000/play/...` з відеоплеєром.

### 🟡 Scenario 7: Idempotency (Тест дублікатів)
1. Залишити завантажене відео в Google Drive. Перезапустити програму.
2. **Expected:** Воркер побачить відео, звірить з `known_files.json` і **пропустить** повторне завантаження.

### 🟡 Scenario 8: Відсутність або помилковий `FolderId`
1. Стерти `"FolderId"` в `secrets.json`. Запустити програму.
2. **Expected:** Worker **не впаде**. Виведе попередження: `GoogleDrive:FolderId is missing...` і засне у циклі-очікуванні.

### 🔴 Scenario 9: Missing Service Account (Fail-fast)
1. Видалити блок `"ServiceAccount"` з `secrets.json`.
2. **Expected:** Програма **впаде** при старті з `InvalidOperationException: ServiceAccount configuration is missing.`.

### 🟢 Scenario 10: Corrupted State (Пошкоджений кеш JSON)
1. Відкрити `known_files.json`, видалити весь текст або написати невалідний JSON.
2. **Expected:** Програма **не впаде**. Напише `Failed to load known files` в лог, обнулить список і почне з чистого аркуша.

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
