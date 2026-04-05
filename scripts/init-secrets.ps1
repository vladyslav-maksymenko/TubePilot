param(
  [string]$OAuthClientSecretPath = "client_secret_*.json",
  [string]$ServiceAccountPath = "yt-autoposter-dev-*.json",
  [string]$OutPath = "TubePilot/TubePilot.Worker/secrets.json",
  [switch]$Force
)

$ErrorActionPreference = "Stop"

function ResolveFirstMatch([string]$pathOrGlob) {
  if (Test-Path -LiteralPath $pathOrGlob) { return (Resolve-Path -LiteralPath $pathOrGlob).Path }
  $matches = Get-ChildItem -File -Filter $pathOrGlob -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($matches) { return $matches.FullName }
  return $null
}

$oauthPath = ResolveFirstMatch $OAuthClientSecretPath
$saPath = ResolveFirstMatch $ServiceAccountPath

if (-not $oauthPath) { throw "OAuth client secret JSON not found (pattern: $OAuthClientSecretPath)." }
if (-not $saPath) { throw "Service account JSON not found (pattern: $ServiceAccountPath)." }

$outFull = (Resolve-Path -LiteralPath (Split-Path -Parent $OutPath) -ErrorAction SilentlyContinue)
if (-not $outFull) {
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutPath) | Out-Null
}

if ((Test-Path -LiteralPath $OutPath) -and (-not $Force)) {
  throw "Refusing to overwrite existing $OutPath. Re-run with -Force if you want to regenerate it."
}

$oauthJson = Get-Content -Raw -LiteralPath $oauthPath | ConvertFrom-Json
$oauth = $oauthJson.installed
if (-not $oauth) { $oauth = $oauthJson.web }
if (-not $oauth) { throw "OAuth JSON missing 'installed' or 'web' section: $oauthPath" }

$serviceAccount = Get-Content -Raw -LiteralPath $saPath | ConvertFrom-Json

$secrets = [ordered]@{
  GoogleDrive = [ordered]@{
    FolderId = "YOUR_GOOGLE_DRIVE_FOLDER_ID"
    ServiceAccount = $serviceAccount
  }
  Telegram = [ordered]@{
    BotToken = "YOUR_TELEGRAM_BOT_TOKEN"
    AllowedChatId = 0
  }
  YouTube = [ordered]@{
    ClientId = [string]$oauth.client_id
    ClientSecret = [string]$oauth.client_secret
    RefreshToken = "YOUR_REFRESH_TOKEN"
  }
  GoogleSheets = [ordered]@{
    SpreadsheetId = "YOUR_SPREADSHEET_ID"
  }
}

$json = ($secrets | ConvertTo-Json -Depth 50)
Set-Content -LiteralPath $OutPath -Value $json -Encoding UTF8

Write-Host "Created $OutPath from:"
Write-Host "- OAuth: $oauthPath"
Write-Host "- ServiceAccount: $saPath"
Write-Host "Next: fill GoogleDrive.FolderId, Telegram.*, YouTube.RefreshToken, GoogleSheets.SpreadsheetId."
