#!/usr/bin/env python3
# точка входа: drive поллинг + телеграм бот + видео сервер

import asyncio
import logging
import signal
import sys

import uvicorn

from config import Config
from drive_monitor import DriveMonitor
from bot import create_bot_app, notify_new_file
import file_tracker

# ── Logging ────────────────────────────────────────────────────
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(name)s] %(levelname)s: %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
logger = logging.getLogger("main")

# глушим шумные либы
logging.getLogger("httpx").setLevel(logging.WARNING)
logging.getLogger("googleapiclient.discovery_cache").setLevel(logging.ERROR)

shutdown_event = asyncio.Event()


async def drive_polling_loop(bot_app):
    """Поллинг Drive по всем каналам."""
    monitors = {}
    for ch in Config.CHANNELS:
        monitors[ch["channel_id"]] = {
            "monitor": DriveMonitor(folder_id=ch["folder_id"], name=ch["name"]),
            "channel": ch,
        }
    logger.info(
        f"Drive monitor started. {len(monitors)} channel(s), "
        f"interval: {Config.POLL_INTERVAL}s"
    )
    for ch in Config.CHANNELS:
        logger.info(f"  📁 {ch['name']} → folder:{ch['folder_id'][:12]}… → chat:{ch['channel_id']}")

    while not shutdown_event.is_set():
        for channel_id, entry in monitors.items():
            monitor = entry["monitor"]
            ch = entry["channel"]
            try:
                new_files = monitor.check_new_files()
                for file_info in new_files:
                    logger.info(f"[{ch['name']}] New file: {file_info['name']}")

                    # Download the file
                    local_path = monitor.download_file(file_info["id"], file_info["name"])

                    # Notify in the correct Telegram channel
                    await notify_new_file(bot_app, file_info, local_path, channel_id)

                    # Mark as processed
                    monitor.mark_processed(file_info["id"])

            except Exception as e:
                logger.error(f"[{ch['name']}] Drive polling error: {e}", exc_info=True)

        # Wait for the poll interval, but break early on shutdown
        try:
            await asyncio.wait_for(shutdown_event.wait(), timeout=Config.POLL_INTERVAL)
            break  # shutdown_event was set
        except asyncio.TimeoutError:
            pass  # Normal — just loop again


async def disk_monitor_loop(bot_app):
    """Проверка места каждый час, алерт если < 15 ГБ."""
    INTERVAL = 3600  # час
    alerted = False  # чтоб не спамить

    while not shutdown_event.is_set():
        try:
            await asyncio.wait_for(shutdown_event.wait(), timeout=INTERVAL)
            break
        except asyncio.TimeoutError:
            pass

        try:
            if file_tracker.is_disk_low():
                if not alerted:
                    status = file_tracker.disk_status_text()
                    text = (
                        f"⚠️ <b>Мало места на диске!</b>\n\n"
                        f"💾 {status}\n"
                        f"Удалите лишние файлы из папки processed/ на сервере."
                    )
                    for ch in Config.CHANNELS:
                        try:
                            await bot_app.bot.send_message(
                                chat_id=ch["channel_id"],
                                text=text,
                                parse_mode="HTML",
                            )
                        except Exception as e:
                            logger.warning(f"Could not send disk alert to {ch['channel_id']}: {e}")
                    alerted = True
                    logger.warning(f"Disk low: {status}")
            else:
                alerted = False  # сброс когда место освобождено

            # чистка опубликованных
            cleaned = file_tracker.cleanup_fully_published()
            if cleaned:
                logger.info(f"Auto-cleaned: {cleaned}")

        except Exception as e:
            logger.error(f"Disk monitor error: {e}", exc_info=True)


async def main():
    # Validate config
    errors = Config.validate()
    if errors:
        for err in errors:
            logger.error(f"Config error: {err}")
        sys.exit(1)

    Config.ensure_dirs()
    logger.info("=" * 50)
    logger.info("Video Processor Bot starting...")
    logger.info("=" * 50)

    # 1. Start FastAPI video server in background
    from player_server import app as fastapi_app

    uvicorn_config = uvicorn.Config(
        fastapi_app,
        host=Config.SERVER_HOST,
        port=Config.SERVER_PORT,
        log_level="warning",
    )
    server = uvicorn.Server(uvicorn_config)
    server_task = asyncio.create_task(server.serve())
    logger.info(
        f"Video server started at {Config.SERVER_PUBLIC_URL} "
        f"(bind {Config.SERVER_HOST}:{Config.SERVER_PORT})"
    )

    # 2. Build and initialize the Telegram bot
    bot_app = create_bot_app()
    await bot_app.initialize()
    await bot_app.start()
    await bot_app.updater.start_polling(drop_pending_updates=True)
    logger.info("Telegram bot started (polling mode)")

    # 3. Start Drive polling loop
    drive_task = asyncio.create_task(drive_polling_loop(bot_app))

    # 4. Start disk space monitor
    disk_task = asyncio.create_task(disk_monitor_loop(bot_app))

    logger.info(f"Disk space: {file_tracker.disk_status_text()}")
    logger.info("All services running. Press Ctrl+C to stop.")

    # Wait for shutdown signal
    loop = asyncio.get_event_loop()

    def _signal_handler():
        logger.info("Shutdown signal received...")
        shutdown_event.set()
        server.should_exit = True

    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, _signal_handler)

    # Wait for the drive task to finish (it will when shutdown_event is set)
    await drive_task
    disk_task.cancel()
    try:
        await disk_task
    except asyncio.CancelledError:
        pass

    # Graceful shutdown
    logger.info("Shutting down services...")

    await bot_app.updater.stop()
    await bot_app.stop()
    await bot_app.shutdown()

    server.should_exit = True
    await server_task

    logger.info("All services stopped. Goodbye!")


if __name__ == "__main__":
    asyncio.run(main())
