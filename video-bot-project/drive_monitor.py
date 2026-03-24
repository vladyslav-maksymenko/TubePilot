import json
import logging
import os
import io

from google.oauth2 import service_account
from googleapiclient.discovery import build
from googleapiclient.http import MediaIoBaseDownload, MediaFileUpload

from config import Config

logger = logging.getLogger(__name__)

KNOWN_FILES_PATH = "known_files.json"
SCOPES = ["https://www.googleapis.com/auth/drive"]


class DriveMonitor:
    def __init__(self, folder_id: str = None, name: str = "default"):
        credentials = service_account.Credentials.from_service_account_file(
            Config.GOOGLE_CREDENTIALS_PATH, scopes=SCOPES
        )
        self.service = build("drive", "v3", credentials=credentials)
        self.folder_id = folder_id or Config.GOOGLE_DRIVE_FOLDER_ID
        self.name = name
        # Per-channel known files to avoid conflicts
        safe_name = name.lower().replace(" ", "_")
        self.known_files_path = f"known_files_{safe_name}.json"
        self.known_files = self._load_known_files()

    def _load_known_files(self) -> dict:
        if os.path.exists(self.known_files_path):
            with open(self.known_files_path, "r") as f:
                return json.load(f)
        return {}

    def _save_known_files(self):
        with open(self.known_files_path, "w") as f:
            json.dump(self.known_files, f, indent=2)

    def check_new_files(self) -> list[dict]:
        """Проверить новые видео в папке Drive."""
        query = (
            f"'{self.folder_id}' in parents "
            f"and mimeType contains 'video/' "
            f"and trashed = false"
        )
        try:
            results = (
                self.service.files()
                .list(
                    q=query,
                    fields="files(id, name, size, mimeType, createdTime)",
                    orderBy="createdTime desc",
                    pageSize=50,
                )
                .execute()
            )
        except Exception as e:
            logger.error(f"Error checking Google Drive: {e}")
            return []

        files = results.get("files", [])
        new_files = []

        for f in files:
            # ещё грузится (size=0)
            file_size = int(f.get("size", 0))
            if file_size == 0:
                logger.debug(f"Skipping {f['name']} — still uploading (size=0)")
                continue

            # проверяем известные файлы
            if f["id"] in self.known_files:
                known = self.known_files[f["id"]]
                known_name = known.get("name", "")
                safe_current = self._sanitize_filename(f["name"])
                safe_known = self._sanitize_filename(known_name)
                if safe_current == safe_known:
                    continue  # имя не менялось
                else:
                    # переименовали — обрабатываем заново
                    logger.info(f"File renamed: '{known_name}' → '{f['name']}' — reprocessing")
                    self.known_files[f["id"]]["name"] = f["name"]
                    self.known_files[f["id"]]["processed"] = False

            # уже скачан
            local_path = os.path.join(Config.DOWNLOAD_DIR, self._sanitize_filename(f["name"]))
            if os.path.exists(local_path):
                logger.info(f"Skipping {f['name']} — already exists locally")
                self.known_files[f["id"]] = {
                    "name": f["name"],
                    "size": f.get("size", "0"),
                    "processed": True,
                }
                continue

            new_files.append(f)
            self.known_files[f["id"]] = {
                "name": f["name"],
                "size": f.get("size", "0"),
                "processed": False,
            }

        if new_files:
            self._save_known_files()
            logger.info(f"Found {len(new_files)} new file(s)")

        return new_files

    @staticmethod
    def _sanitize_filename(filename: str) -> str:
        """Убираем проблемные символы из имени файла."""
        # кавычки и скобки ломают ffmpeg
        for ch in ['"', "'", '`', '(', ')', '[', ']', '{', '}']:
            filename = filename.replace(ch, '')
        # Replace spaces with underscores
        filename = filename.replace(' ', '_')
        # Remove double underscores
        while '__' in filename:
            filename = filename.replace('__', '_')
        return filename

    def download_file(self, file_id: str, filename: str) -> str:
        """Скачать файл с Drive."""
        Config.ensure_dirs()
        filename = self._sanitize_filename(filename)
        output_path = os.path.join(Config.DOWNLOAD_DIR, filename)

        request = self.service.files().get_media(fileId=file_id)
        with open(output_path, "wb") as f:
            downloader = MediaIoBaseDownload(f, request)
            done = False
            while not done:
                status, done = downloader.next_chunk()
                if status:
                    logger.info(f"Download {filename}: {int(status.progress() * 100)}%")

        logger.info(f"Downloaded: {output_path}")
        return output_path

    def mark_processed(self, file_id: str):
        if file_id in self.known_files:
            self.known_files[file_id]["processed"] = True
            self._save_known_files()

    def get_file_size_mb(self, file_info: dict) -> float:
        size_bytes = int(file_info.get("size", 0))
        return round(size_bytes / (1024 * 1024), 1)
