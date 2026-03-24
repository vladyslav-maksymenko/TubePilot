# конфиг из .env

import os
from pathlib import Path
from dotenv import load_dotenv

load_dotenv()


class Config:
    # Telegram
    TELEGRAM_BOT_TOKEN: str = os.getenv("TELEGRAM_BOT_TOKEN", "")
    TELEGRAM_CHANNEL_ID: str = os.getenv("TELEGRAM_CHANNEL_ID", "")

    # Google Drive
    GOOGLE_DRIVE_FOLDER_ID: str = os.getenv("GOOGLE_DRIVE_FOLDER_ID", "")
    GOOGLE_CREDENTIALS_PATH: str = os.getenv("GOOGLE_CREDENTIALS_PATH", "credentials.json")

    # Server (video player)
    SERVER_HOST: str = os.getenv("SERVER_HOST", "0.0.0.0")
    SERVER_PORT: int = int(os.getenv("SERVER_PORT", "8080"))
    SERVER_PUBLIC_URL: str = os.getenv("SERVER_PUBLIC_URL", "http://localhost:8080")

    # Directories
    DOWNLOAD_DIR: str = os.getenv("DOWNLOAD_DIR", "downloads")
    PROCESSED_DIR: str = os.getenv("PROCESSED_DIR", "processed")

    # Polling interval (seconds)
    POLL_INTERVAL: int = int(os.getenv("POLL_INTERVAL", "60"))

    # Video slicing duration (seconds)
    SLICE_DURATION: int = int(os.getenv("SLICE_DURATION", "180"))

    # Dohoo.ai (YouTube publishing)
    DOHOO_API_URL: str = os.getenv("DOHOO_API_URL", "")
    DOHOO_API_KEY: str = os.getenv("DOHOO_API_KEY", "")

    # QR оверлей
    QR_OVERLAY_PATH: str = os.getenv("QR_OVERLAY_PATH", "patreon sling.png")

    # Channels (built from .env)
    CHANNELS: list[dict] = []

    @classmethod
    def load_channels(cls):
        """Собираем каналы из .env: CHANNEL_1 из базовых, CHANNEL_2+ из CHANNEL_N_*."""
        cls.CHANNELS = []

        # Channel 1 — from existing env vars
        if cls.GOOGLE_DRIVE_FOLDER_ID and cls.TELEGRAM_CHANNEL_ID:
            cls.CHANNELS.append({
                "name": os.getenv("CHANNEL_1_NAME", "Main"),
                "folder_id": cls.GOOGLE_DRIVE_FOLDER_ID,
                "channel_id": cls.TELEGRAM_CHANNEL_ID,
            })

        # Channel 2, 3, 4, ... — from CHANNEL_N_* env vars
        n = 2
        while True:
            folder_id = os.getenv(f"CHANNEL_{n}_FOLDER_ID")
            channel_id = os.getenv(f"CHANNEL_{n}_CHANNEL_ID")
            if not folder_id or not channel_id:
                break
            cls.CHANNELS.append({
                "name": os.getenv(f"CHANNEL_{n}_NAME", f"Channel {n}"),
                "folder_id": folder_id,
                "channel_id": channel_id,
            })
            n += 1

    @classmethod
    def validate(cls) -> list[str]:
        """Проверка конфига, возвращает список ошибок."""
        errors = []
        if not cls.TELEGRAM_BOT_TOKEN:
            errors.append("TELEGRAM_BOT_TOKEN is not set")
        cls.load_channels()
        if not cls.CHANNELS:
            errors.append("No channels configured (set GOOGLE_DRIVE_FOLDER_ID + TELEGRAM_CHANNEL_ID)")
        if not Path(cls.GOOGLE_CREDENTIALS_PATH).exists():
            errors.append(f"Credentials file not found: {cls.GOOGLE_CREDENTIALS_PATH}")
        return errors

    @classmethod
    def ensure_dirs(cls):
        """Создать папки если нет."""
        os.makedirs(cls.DOWNLOAD_DIR, exist_ok=True)
        os.makedirs(cls.PROCESSED_DIR, exist_ok=True)
