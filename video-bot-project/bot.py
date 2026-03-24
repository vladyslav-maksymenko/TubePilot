import asyncio
import hashlib
import json
import logging
import os
import shutil
import subprocess
import threading
import time
import urllib.parse
from concurrent.futures import ThreadPoolExecutor

from telegram import Update, InlineKeyboardButton, InlineKeyboardMarkup, InputFile
from telegram.ext import (
    Application,
    CallbackQueryHandler,
    CommandHandler,
    ContextTypes,
    MessageHandler,
    filters,
)

from config import Config
from video_processor import VideoProcessor
from dohoo_publisher import get_youtube_connections, publish_to_youtube
import file_tracker

logger = logging.getLogger(__name__)

# --- стейт ---
user_selections: dict[int, dict] = {}    # msg_id -> {file_id, file_name, local_path, options}
publish_sessions: dict[int, dict] = {}   # chat_id -> {video_path, step, connection_id, ...}
rename_sessions: dict[int, dict] = {}    # chat_id -> {connection_id, msg_id}
video_registry: dict[str, str] = {}      # short_id -> video_path (кнопки публикации)

# алиасы ютуб каналов
ALIASES_PATH = os.path.join(os.path.dirname(__file__), "youtube_aliases.json")

def _load_aliases() -> dict:
    if os.path.exists(ALIASES_PATH):
        with open(ALIASES_PATH, "r") as f:
            return json.load(f)
    return {}

def _save_aliases(aliases: dict):
    with open(ALIASES_PATH, "w") as f:
        json.dump(aliases, f, indent=2, ensure_ascii=False)

def _get_channel_name(connection_id, platform_username: str) -> str:
    """Алиас канала или оригинальное имя."""
    aliases = _load_aliases()
    return aliases.get(str(connection_id), platform_username)


def _init_video_registry():
    """Загружаем реестр видео чтобы кнопки публикации работали после рестарта."""
    processed = Config.PROCESSED_DIR
    if os.path.exists(processed):
        for filename in os.listdir(processed):
            filepath = os.path.join(processed, filename)
            if os.path.isfile(filepath):
                short_id = hashlib.md5(filepath.encode()).hexdigest()[:8]
                video_registry[short_id] = filepath
        if video_registry:
            logger.info(f"Loaded {len(video_registry)} videos into registry")


_init_video_registry()

processor = VideoProcessor()
executor = ThreadPoolExecutor(max_workers=1)

# очередь обработки
_queue_lock = threading.Lock()
_active_tasks = [0]


# --- клавиатуры ---

def build_options_keyboard(message_id: int, file_id: str) -> InlineKeyboardMarkup:
    """Клавиатура с чекбоксами опций обработки."""
    state = user_selections.get(message_id, {})
    selected = state.get("options", set())

    buttons = []
    for opt_id in VideoProcessor.OPTION_IDS:
        label = VideoProcessor.OPTION_LABELS[opt_id]
        check = "✅" if opt_id in selected else "☐"
        buttons.append([
            InlineKeyboardButton(
                f"{check} {label}",
                callback_data=f"toggle|{opt_id}|{file_id}",
            )
        ])

    buttons.append([
        InlineKeyboardButton("📌 Выбрать всё", callback_data=f"select_all|{file_id}"),
        InlineKeyboardButton("❌ Сбросить", callback_data=f"deselect_all|{file_id}"),
    ])

    buttons.append([
        InlineKeyboardButton(
            "🚀 Начать обработку",
            callback_data=f"start|{file_id}",
        )
    ])

    return InlineKeyboardMarkup(buttons)


def build_result_keyboard(video_path: str) -> InlineKeyboardMarkup:
    """Кнопка 'Опубликовать' под результатом."""
    short_id = hashlib.md5(video_path.encode()).hexdigest()[:8]
    video_registry[short_id] = video_path
    return InlineKeyboardMarkup([
        [InlineKeyboardButton("📤 Опубликовать на YouTube", callback_data=f"pub|{short_id}")],
    ])


def _generate_thumbnail(video_path: str) -> str | None:
    """Генерим превью через ffmpeg."""
    thumb_path = video_path.rsplit(".", 1)[0] + "_thumb.jpg"
    try:
        subprocess.run(
            [
                "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
                "-i", video_path,
                "-ss", "1",
                "-vframes", "1",
                "-q:v", "3",
                thumb_path,
            ],
            check=True,
            timeout=15,
        )
        if os.path.exists(thumb_path):
            return thumb_path
    except Exception as e:
        logger.warning(f"Thumbnail generation failed: {e}")
    return None


def _get_duration(video_path: str) -> str:
    """Длительность видео строкой (2:34)."""
    try:
        result = subprocess.run(
            [
                "ffprobe", "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                video_path,
            ],
            capture_output=True, text=True, timeout=10,
        )
        seconds = float(result.stdout.strip())
        mins = int(seconds // 60)
        secs = int(seconds % 60)
        return f"{mins}:{secs:02d}"
    except Exception:
        return "?:??"


def build_channel_keyboard(channels: list[dict]) -> InlineKeyboardMarkup:
    """Кнопки выбора ютуб канала."""
    buttons = []
    for ch in channels:
        name = _get_channel_name(ch['id'], ch['platformUsername'])
        buttons.append([InlineKeyboardButton(f"📺 {name}", callback_data=f"yt_ch|{ch['id']}")])
    buttons.append([InlineKeyboardButton("❌ Отмена", callback_data="yt_cancel")])
    return InlineKeyboardMarkup(buttons)


# --- прогресс ---

def _progress_bar(current: int, total: int, width: int = 10) -> str:
    filled = round(width * current / total) if total > 0 else 0
    bar = "▓" * filled + "░" * (width - filled)
    pct = round(100 * current / total) if total > 0 else 0
    return f"{bar} {pct}%"


async def _update_progress(app: Application, chat_id: int, msg_id: int,
                           step: int, total: int, desc: str, file_name: str):
    bar = _progress_bar(step, total)
    text = (
        f"⚙️ <b>Обработка</b>\n\n"
        f"📁 <code>{file_name}</code>\n\n"
        f"📊 {bar}\n"
        f"🔄 <i>{desc}</i>  ({step}/{total})"
    )
    try:
        await app.bot.edit_message_text(
            chat_id=chat_id, message_id=msg_id,
            text=text, parse_mode="HTML",
        )
    except Exception as e:
        if "Retry" in str(e):
            await asyncio.sleep(5)


# --- уведомление о новом файле ---

async def notify_new_file(app: Application, file_info: dict, local_path: str,
                          channel_id: str = None):
    file_id = file_info["id"]
    file_name = file_info["name"]
    size_mb = int(file_info.get("size", 0)) / (1024 * 1024)
    target_chat = channel_id or Config.TELEGRAM_CHANNEL_ID

    text = (
        f"🎬 <b>Новое видео</b>\n\n"
        f"📁 <code>{file_name}</code>\n"
        f"📊 {size_mb:.1f} МБ\n\n"
        f"👇 Выберите опции и нажмите <b>Начать</b>:"
    )

    temp_state = {
        "file_id": file_id,
        "file_name": file_name,
        "local_path": local_path,
        "options": set(),
    }

    msg = await app.bot.send_message(
        chat_id=target_chat,
        text=text,
        parse_mode="HTML",
        reply_markup=build_options_keyboard(0, file_id),
    )

    user_selections[msg.message_id] = temp_state
    logger.info(f"Sent notification for {file_name} (msg_id={msg.message_id})")


# --- обработка колбеков ---

async def handle_callback(update: Update, context: ContextTypes.DEFAULT_TYPE):
    query = update.callback_query

    try:
        await query.answer()
    except Exception:
        pass  # просроченный колбек, но всё равно обрабатываем

    data = query.data
    msg_id = query.message.message_id
    chat_id = query.message.chat_id

    # тоггл опции
    if data.startswith("toggle|"):
        _, opt_id, file_id = data.split("|", 2)
        if msg_id not in user_selections:
            await query.answer("⚠️ Сессия устарела", show_alert=True)
            return
        state = user_selections[msg_id]
        if opt_id in state["options"]:
            state["options"].discard(opt_id)
        else:
            state["options"].add(opt_id)
        await query.answer()
        await query.message.edit_reply_markup(
            reply_markup=build_options_keyboard(msg_id, file_id)
        )

    # выбрать / сбросить всё
    elif data.startswith("select_all|"):
        _, file_id = data.split("|", 1)
        if msg_id in user_selections:
            user_selections[msg_id]["options"] = set(VideoProcessor.OPTION_IDS)
            await query.answer()
            await query.message.edit_reply_markup(
                reply_markup=build_options_keyboard(msg_id, file_id)
            )

    elif data.startswith("deselect_all|"):
        _, file_id = data.split("|", 1)
        if msg_id in user_selections:
            user_selections[msg_id]["options"] = set()
            await query.answer()
            await query.message.edit_reply_markup(
                reply_markup=build_options_keyboard(msg_id, file_id)
            )

    # запуск обработки
    elif data.startswith("start|"):
        _, file_id = data.split("|", 1)
        if msg_id not in user_selections:
            await query.answer("⚠️ Сессия устарела", show_alert=True)
            return

        state = user_selections[msg_id]
        selected = state["options"]

        if not selected:
            await query.answer("⚠️ Выберите хотя бы одну опцию!", show_alert=True)
            return

        # очередь
        with _queue_lock:
            _active_tasks[0] += 1
            queue_pos = _active_tasks[0]

        if queue_pos > 1:
            await query.message.edit_text(
                f"⏳ <b>В очереди</b>\n\n"
                f"📁 <code>{state['file_name']}</code>\n"
                f"📊 Позиция: {queue_pos - 1}\n"
                f"🔄 <i>Ожидание...</i>",
                parse_mode="HTML",
            )
        else:
            await query.message.edit_text(
                f"⚙️ <b>Обработка</b>\n\n"
                f"📁 <code>{state['file_name']}</code>\n\n"
                f"📊 {_progress_bar(0, 1)}\n"
                f"🔄 <i>Запуск...</i>",
                parse_mode="HTML",
            )
        await query.answer()
        loop = asyncio.get_running_loop()
        loop.run_in_executor(
            executor,
            _process_sync,
            state["local_path"],
            list(selected),
            msg_id,
            chat_id,
            context.application,
            loop,
            state["file_name"],
        )

    # публикация
    elif data.startswith("pub|"):
        short_id = data.split("|", 1)[1]
        video_path = video_registry.get(short_id)

        if not video_path or not os.path.exists(video_path):
            await query.answer("⚠️ Файл не найден", show_alert=True)
            return

        # получаем каналы
        try:
            channels = await get_youtube_connections()
        except Exception as e:
            await query.answer(f"❌ API: {str(e)[:150]}", show_alert=True)
            return

        if not channels:
            await query.answer("❌ Нет YouTube каналов", show_alert=True)
            return

        filename = os.path.basename(video_path)
        publish_sessions[chat_id] = {
            "step": "choose_channel",
            "video_path": video_path,
            "filename": filename,
            "channels": channels,
            "original_msg_id": query.message.message_id,
            "original_chat_id": chat_id,
        }

        await query.answer()

        await query.message.reply_text(
            f"📤 <b>Публикация на YouTube</b>\n\n"
            f"📁 <code>{filename}</code>\n\n"
            f"Выберите канал:",
            parse_mode="HTML",
            reply_markup=build_channel_keyboard(channels),
        )

    # выбран канал
    elif data.startswith("yt_ch|"):
        connection_id = int(data.split("|", 1)[1])
        session = publish_sessions.get(chat_id)
        if not session or session["step"] != "choose_channel":
            await query.answer("⚠️ Сессия устарела", show_alert=True)
            return

        ch_name = "YouTube"
        for ch in session["channels"]:
            if ch["id"] == connection_id:
                ch_name = _get_channel_name(ch['id'], ch['platformUsername'])
                break

        session["step"] = "enter_title"
        session["connection_id"] = connection_id
        session["channel_name"] = ch_name

        await query.answer()
        await query.message.edit_text(
            f"📤 <b>Публикация на YouTube</b>\n\n"
            f"📁 <code>{session['filename']}</code>\n"
            f"📺 {ch_name}\n\n"
            f"✏️ Отправьте <b>название</b> видео:\n"
            f"<i>(/skip — использовать имя файла)</i>",
            parse_mode="HTML",
        )
        session["status_msg_id"] = query.message.message_id

    # переименование канала (из /channels)
    elif data.startswith("ch_rename|"):
        connection_id = data.split("|", 1)[1]
        rename_sessions[chat_id] = {
            "connection_id": connection_id,
            "msg_id": query.message.message_id,
        }
        await query.message.edit_text(
            "✏️ Отправьте новое название для этого канала:",
            parse_mode="HTML",
        )

    # noop (кнопка "опубликовано")
    elif data == "noop":
        await query.answer()

    # отмена публикации
    elif data == "yt_cancel":
        publish_sessions.pop(chat_id, None)
        await query.answer()
        await query.message.edit_text("❌ Публикация отменена.")


# --- текстовые сообщения ---

async def handle_text(update: Update, context: ContextTypes.DEFAULT_TYPE):
    """Обработка текста: переименование, титул, описание."""
    msg = update.effective_message
    if not msg or not msg.text:
        return

    chat_id = update.effective_chat.id
    text = msg.text.strip()
    bot = context.bot

    # проверяем сессию переименования
    rename = rename_sessions.get(chat_id)
    if rename:
        conn_id = rename["connection_id"]
        aliases = _load_aliases()
        aliases[str(conn_id)] = text
        _save_aliases(aliases)
        logger.info(f"YouTube channel {conn_id} renamed to: {text}")
        try:
            await msg.delete()
        except Exception:
            pass
        try:
            await bot.edit_message_text(
                chat_id=chat_id,
                message_id=rename["msg_id"],
                text=f"✅ Канал переименован в <b>{text}</b>",
                parse_mode="HTML",
            )
        except Exception:
            pass
        rename_sessions.pop(chat_id, None)
        return

    # проверяем сессию публикации
    session = publish_sessions.get(chat_id)
    if not session:
        return

    status_msg_id = session.get("status_msg_id")

    if session["step"] == "enter_title":
        session["title"] = os.path.splitext(session["filename"])[0] if text == "/skip" else text
        session["step"] = "enter_description"

        # Edit the status message with updated info
        if status_msg_id:
            try:
                await bot.edit_message_text(
                    chat_id=chat_id,
                    message_id=status_msg_id,
                    text=(
                        f"📤 <b>Публикация на YouTube</b>\n\n"
                        f"📁 <code>{session['filename']}</code>\n"
                        f"📺 {session['channel_name']}\n"
                        f"📝 {session['title']}\n\n"
                        f"✏️ Отправьте <b>описание</b> видео:\n"
                        f"<i>(/skip — пропустить)</i>"
                    ),
                    parse_mode="HTML",
                )
            except Exception as e:
                logger.warning(f"Could not edit status message: {e}")

        # удаляем сообщение юзера
        try:
            await msg.delete()
        except Exception:
            pass

    elif session["step"] == "enter_description":
        session["description"] = "" if text == "/skip" else text
        session["step"] = "publishing"

        # удаляем сообщение юзера
        try:
            await msg.delete()
        except Exception:
            pass

        # Update status to "publishing..."
        filename = session["filename"]
        if status_msg_id:
            try:
                await bot.edit_message_text(
                    chat_id=chat_id,
                    message_id=status_msg_id,
                    text=(
                        f"⏳ <b>Публикация...</b>\n\n"
                        f"📺 {session['channel_name']}\n"
                        f"📝 {session['title']}"
                    ),
                    parse_mode="HTML",
                )
            except Exception as e:
                logger.warning(f"Could not edit status message: {e}")

        encoded = urllib.parse.quote(filename)
        file_url = f"{Config.SERVER_PUBLIC_URL}/video/{encoded}"

        try:
            await publish_to_youtube(
                file_url=file_url,
                connection_id=session["connection_id"],
                title=session["title"],
                description=session["description"],
                caption=session["title"],
            )
            # отмечаем как опубликованное
            file_tracker.mark_published(session["video_path"])

            # чистим если всё опубликовано
            cleaned = file_tracker.cleanup_fully_published()
            if cleaned:
                logger.info(f"Auto-cleaned fully published: {cleaned}")

            result_text = (
                f"✅ <b>Опубликовано на YouTube!</b>\n\n"
                f"📺 {session['channel_name']}\n"
                f"📝 {session['title']}"
            )
            published_ok = True
        except Exception as e:
            result_text = (
                f"❌ <b>Ошибка публикации</b>\n\n"
                f"<code>{str(e)[:500]}</code>"
            )
            published_ok = False

        if status_msg_id:
            try:
                await bot.edit_message_text(
                    chat_id=chat_id,
                    message_id=status_msg_id,
                    text=result_text,
                    parse_mode="HTML",
                )
            except Exception as e:
                logger.warning(f"Could not edit status message: {e}")

        # отмечаем оригинальное сообщение
        if published_ok:
            orig_msg_id = session.get("original_msg_id")
            orig_chat_id = session.get("original_chat_id")
            if orig_msg_id and orig_chat_id:
                try:
                    published_markup = InlineKeyboardMarkup([
                        [InlineKeyboardButton(
                            f"✅ Опубликовано — {session['channel_name']}",
                            callback_data="noop",
                        )],
                    ])
                    await bot.edit_message_reply_markup(
                        chat_id=orig_chat_id,
                        message_id=orig_msg_id,
                        reply_markup=published_markup,
                    )
                except Exception as e:
                    logger.warning(f"Could not mark original as published: {e}")

        publish_sessions.pop(chat_id, None)


# --- обработка в потоке ---

def _process_sync(local_path: str, selected: list[str], msg_id: int,
                  chat_id: int, app: Application, loop: asyncio.AbstractEventLoop,
                  file_name: str):

    # Estimate total steps
    slice_opts = {"slice", "slice_long"}
    has_slice = "slice" in selected
    has_slice_long = "slice_long" in selected
    do_both_slices = has_slice and has_slice_long
    do_slice = has_slice or has_slice_long
    non_slice = [o for o in selected if o not in slice_opts]

    _last_progress_time = [0.0]

    def send_progress(step, total, desc, force=False):
        now = time.time()
        if not force and (now - _last_progress_time[0]) < 5:
            return  # не чаще раз в 5 сек
        _last_progress_time[0] = now
        asyncio.run_coroutine_threadsafe(
            _update_progress(app, chat_id, msg_id, step, total, desc, file_name),
            loop,
        )

    def _slice_and_process(local_path, min_dur, max_dur, min_left, label, non_slice):
        """Нарезать + обработать."""
        send_progress(0, 2, f"Нарезка {label}...", force=True)
        sliced = processor.slice_video(local_path, min_dur=min_dur, max_dur=max_dur, min_leftover=min_left)
        num = len(sliced)
        send_progress(1, 1 + num, f"Нарезано {label}: {num} частей")

        if non_slice:
            results, descs = [], []
            for i, part in enumerate(sliced, 1):
                def make_cb(pi, pt):
                    def cb(pct):
                        send_progress(pi, pt, f"{label} часть {pi}/{pt}... {pct}%")
                    return cb
                out, desc = processor._apply_combined(part, non_slice, progress_cb=make_cb(i, num))
                results.append(out)
                descs.append(desc)
            return results, descs
        else:
            return sliced, [f"Нарезка {label}"] * num

    try:
        if do_both_slices:
            # обе нарезки — сначала короткие, потом длинные
            r_short, d_short = _slice_and_process(
                local_path, 150, 190, 30, "2:30–3:10", non_slice)
            send_progress(1, 2, "Короткая нарезка готова, отправка...", force=True)

            # Send short batch
            future1 = asyncio.run_coroutine_threadsafe(
                _send_results(app, chat_id, msg_id, r_short, file_name,
                              selected, d_short, batch_label="Нарезка 2:30–3:10"),
                loop,
            )
            try:
                future1.result(timeout=120)
            except Exception as e:
                logger.error(f"Error sending short batch: {e}", exc_info=True)

            r_long, d_long = _slice_and_process(
                local_path, 310, 430, 60, "5:10–7:10", non_slice)
            send_progress(2, 2, "Готово!", force=True)

            # Send long batch
            future2 = asyncio.run_coroutine_threadsafe(
                _send_results(app, chat_id, msg_id, r_long, file_name,
                              selected, d_long, batch_label="Нарезка 5:10–7:10"),
                loop,
            )
            try:
                future2.result(timeout=120)
            except Exception as e:
                logger.error(f"Error sending long batch: {e}", exc_info=True)

            results = r_short + r_long
            processing_descs = d_short + d_long

        elif do_slice:
            if has_slice_long:
                results, processing_descs = _slice_and_process(
                    local_path, 310, 430, 60, "5:10–7:10", non_slice)
            else:
                results, processing_descs = _slice_and_process(
                    local_path, 150, 190, 30, "2:30–3:10", non_slice)
            send_progress(1, 1, "Готово!", force=True)
        else:
            send_progress(0, 1, "Обработка видео...", force=True)

            def progress_cb(pct):
                send_progress(0, 1, f"Обработка видео... {pct}%")

            out, desc = processor._apply_combined(
                local_path, non_slice, progress_cb=progress_cb)
            results = [out]
            processing_descs = [desc]
            send_progress(1, 1, "Готово!", force=True)

        if not do_both_slices:
            # для одной нарезки или без нарезки — отправляем тут
            future = asyncio.run_coroutine_threadsafe(
                _send_results(app, chat_id, msg_id, results, file_name, selected, processing_descs),
                loop,
            )
            # ждём результат
            try:
                future.result(timeout=120)
            except Exception as send_err:
                logger.error(f"Error sending results: {send_err}", exc_info=True)

        # удаляем исходник
        try:
            if os.path.exists(local_path):
                os.remove(local_path)
                logger.info(f"Deleted source file: {local_path}")
        except Exception as del_err:
            logger.warning(f"Could not delete source file {local_path}: {del_err}")

    except Exception as e:
        logger.error(f"Processing error: {e}", exc_info=True)
        asyncio.run_coroutine_threadsafe(
            _send_error(app, chat_id, msg_id, str(e)),
            loop,
        )
    finally:
        with _queue_lock:
            _active_tasks[0] -= 1


# --- отправка результатов ---

async def _send_results(app: Application, chat_id: int, msg_id: int,
                        results: list[str], file_name: str, selected: list[str],
                        processing_descs: list[str] = None,
                        batch_label: str = None):
    selected_labels = [VideoProcessor.OPTION_LABELS[o] for o in selected if o in VideoProcessor.OPTION_LABELS]
    total = len(results)
    if processing_descs is None:
        processing_descs = [""] * total

    # Clean base name (without extension) for display
    base_name = os.path.splitext(file_name)[0]

    logger.info(f"Sending {total} result messages for {file_name}" +
                (f" [{batch_label}]" if batch_label else ""))

    # Edit original message to show completion
    header = f"✅ <b>{batch_label}</b>" if batch_label else "✅ <b>Обработка завершена!</b>"
    await _safe_edit(
        app, chat_id, msg_id,
        f"{header}\n\n"
        f"📁 <code>{file_name}</code>\n"
        f"🔧 {', '.join(selected_labels)}\n"
        f"📦 {total} видео"
    )

    # отправляем каждое видео отдельным сообщением
    for i, path in enumerate(results, 1):
        try:
            filename = os.path.basename(path)
            encoded = urllib.parse.quote(filename)
            url = f"{Config.SERVER_PUBLIC_URL}/play/{encoded}"
            size_mb = os.path.getsize(path) / (1024 * 1024)
            duration = _get_duration(path)
            desc = processing_descs[i - 1] if i - 1 < len(processing_descs) else ""

            if total > 1:
                header = f"📹 <b>Часть {i}/{total}</b> — {base_name}"
            else:
                header = f"📹 <b>Результат</b> — {base_name}"

            caption = f"{header}\n\n"
            caption += f"⏱ {duration}  ·  📊 {size_mb:.1f} МБ\n"
            if desc:
                caption += f"🔧 {desc}\n"
            caption += f"\n▶️ <a href=\"{url}\">Смотреть</a>"

            # Generate and send thumbnail
            thumb = _generate_thumbnail(path)
            if thumb:
                with open(thumb, "rb") as f:
                    await app.bot.send_photo(
                        chat_id=chat_id,
                        photo=f,
                        caption=caption,
                        parse_mode="HTML",
                        reply_markup=build_result_keyboard(path),
                    )
                try:
                    os.remove(thumb)
                except Exception:
                    pass
            else:
                await app.bot.send_message(
                    chat_id=chat_id,
                    text=caption,
                    parse_mode="HTML",
                    disable_web_page_preview=True,
                    reply_markup=build_result_keyboard(path),
                )
            logger.info(f"Sent result {i}/{total}: {filename}")

            # пауза между сообщениями от рейтлимита
            if i < total:
                await asyncio.sleep(1.5)

        except Exception as e:
            logger.error(f"Error sending result {i}/{total}: {e}", exc_info=True)
            # фолбек без клавиатуры
            try:
                await app.bot.send_message(
                    chat_id=chat_id,
                    text=f"📹 Часть {i}: {url}",
                    disable_web_page_preview=True,
                )
            except Exception:
                pass

    # сохраняем в историю
    output_paths = results if results else []
    file_tracker.record_outputs(
        source_file_id=file_name,
        source_name=file_name,
        output_paths=output_paths,
    )

    # Clean up
    user_selections.pop(msg_id, None)
    logger.info(f"All results sent for {file_name}")


async def _safe_edit(app: Application, chat_id: int, msg_id: int, text: str):
    """Редактирование с фолбеком на send."""
    for attempt in range(3):
        try:
            await app.bot.edit_message_text(
                chat_id=chat_id, message_id=msg_id,
                text=text, parse_mode="HTML",
                disable_web_page_preview=True,
            )
            return
        except Exception as e:
            if "Retry" in str(e) and attempt < 2:
                wait = 10 * (attempt + 1)
                logger.warning(f"Flood control, waiting {wait}s...")
                await asyncio.sleep(wait)
            else:
                try:
                    await app.bot.send_message(
                        chat_id=chat_id, text=text,
                        parse_mode="HTML", disable_web_page_preview=True,
                    )
                except Exception:
                    pass
                return


async def _send_error(app: Application, chat_id: int, msg_id: int, error: str):
    text = f"❌ <b>Ошибка обработки</b>\n\n<code>{error[:500]}</code>"
    await _safe_edit(app, chat_id, msg_id, text)


# --- команды ---

async def cmd_start(update: Update, context: ContextTypes.DEFAULT_TYPE):
    await update.message.reply_text(
        "👋 <b>Video Processor Bot</b>\n\n"
        "Я мониторю Google Drive и уведомляю о новых видео.\n\n"
        "🔧 <b>Доступная обработка:</b>\n"
        + "\n".join(f"  • {lbl}" for lbl in VideoProcessor.OPTION_LABELS.values())
        + "\n\n📤 После обработки можно опубликовать на YouTube через dohoo.ai",
        parse_mode="HTML",
    )


async def cmd_status(update: Update, context: ContextTypes.DEFAULT_TYPE):
    if not user_selections:
        await update.message.reply_text("📭 Нет активных сессий.")
        return

    lines = ["📋 <b>Активные сессии:</b>\n"]
    for mid, state in user_selections.items():
        opts = ", ".join(VideoProcessor.OPTION_LABELS[o] for o in state["options"]) or "—"
        lines.append(f"• <code>{state['file_name']}</code> — {opts}")

    await update.message.reply_text("\n".join(lines), parse_mode="HTML")


async def cmd_channels(update: Update, context: ContextTypes.DEFAULT_TYPE):
    """Показать ютуб каналы с кнопками переименования."""
    try:
        channels = await get_youtube_connections()
    except Exception as e:
        await update.message.reply_text(f"❌ Ошибка API: {str(e)[:200]}")
        return

    if not channels:
        await update.message.reply_text("❌ Нет подключённых YouTube каналов")
        return

    buttons = []
    lines = ["📺 <b>Подключённые YouTube каналы:</b>\n"]
    for ch in channels:
        name = _get_channel_name(ch['id'], ch['platformUsername'])
        original = ch['platformUsername']
        if name != original:
            lines.append(f"📺 <b>{name}</b> <i>({original})</i>")
        else:
            lines.append(f"📺 <b>{name}</b>")
        buttons.append([InlineKeyboardButton(
            f"✏️ Переименовать {name}",
            callback_data=f"ch_rename|{ch['id']}",
        )])

    await update.message.reply_text(
        "\n".join(lines),
        parse_mode="HTML",
        reply_markup=InlineKeyboardMarkup(buttons),
    )


# --- инициализация ---

def create_bot_app() -> Application:
    app = Application.builder().token(Config.TELEGRAM_BOT_TOKEN).build()
    app.add_handler(CommandHandler("start", cmd_start))
    app.add_handler(CommandHandler("status", cmd_status))
    app.add_handler(CommandHandler("channels", cmd_channels))
    app.add_handler(CallbackQueryHandler(handle_callback))
    app.add_handler(MessageHandler(filters.TEXT & ~filters.COMMAND, handle_text))
    return app
