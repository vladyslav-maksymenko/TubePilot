# трекинг обработанных файлов и статус публикации

import json
import logging
import os
import shutil
import time

from config import Config

logger = logging.getLogger(__name__)

HISTORY_PATH = os.path.join(os.path.dirname(__file__), "processed_history.json")
DISK_WARN_BYTES = 15 * 1024 ** 3  # 15 GB


# --- чтение/запись ---

def _load() -> dict:
    if os.path.exists(HISTORY_PATH):
        try:
            with open(HISTORY_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {}


def _save(data: dict):
    with open(HISTORY_PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)


# --- API ---

def record_outputs(source_file_id: str, source_name: str, output_paths: list[str]):
    """Записать список обработанных файлов."""
    data = _load()
    outputs = {
        os.path.basename(p): {
            "path": p,
            "published": False,
            "published_at": None,
        }
        for p in output_paths
    }
    entry = data.get(source_file_id, {})
    entry.update({
        "source_name": source_name,
        "processed_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "outputs": {**entry.get("outputs", {}), **outputs},
        "cleaned": False,
    })
    data[source_file_id] = entry
    _save(data)
    logger.info(f"Tracked {len(outputs)} output(s) for '{source_name}'")


def mark_published(output_path: str):
    """Отметить файл как опубликованный."""
    data = _load()
    filename = os.path.basename(output_path)
    changed = False
    for file_id, entry in data.items():
        if filename in entry.get("outputs", {}):
            entry["outputs"][filename]["published"] = True
            entry["outputs"][filename]["published_at"] = time.strftime("%Y-%m-%d %H:%M:%S")
            changed = True
            logger.info(f"Marked as published: {filename}")
            break
    if changed:
        _save(data)


def cleanup_fully_published():
    """Удалить файлы где все нарезки опубликованы."""
    data = _load()
    cleaned = []
    for file_id, entry in data.items():
        if entry.get("cleaned"):
            continue
        outputs = entry.get("outputs", {})
        if not outputs:
            continue
        all_published = all(o["published"] for o in outputs.values())
        if all_published:
            for filename, info in outputs.items():
                path = info.get("path", "")
                if path and os.path.exists(path):
                    try:
                        os.remove(path)
                        logger.info(f"Auto-deleted (fully published): {path}")
                    except Exception as e:
                        logger.warning(f"Could not delete {path}: {e}")
            entry["cleaned"] = True
            cleaned.append(entry.get("source_name", file_id))
    if cleaned:
        _save(data)
    return cleaned


# --- диск ---

def get_free_bytes() -> int:
    usage = shutil.disk_usage(Config.PROCESSED_DIR if os.path.exists(Config.PROCESSED_DIR) else "/")
    return usage.free


def is_disk_low() -> bool:
    return get_free_bytes() < DISK_WARN_BYTES


def disk_status_text() -> str:
    free_gb = get_free_bytes() / 1024 ** 3
    return f"{free_gb:.1f} ГБ свободно"
