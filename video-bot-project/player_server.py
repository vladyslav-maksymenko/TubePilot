"""FastAPI server for video streaming and player page."""

import logging
import os
import mimetypes

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import HTMLResponse, StreamingResponse, Response

from config import Config

logger = logging.getLogger(__name__)

app = FastAPI()


@app.get("/play/{filename}")
async def play(filename: str):
    """Serve the HTML video player page."""
    filepath = os.path.join(Config.PROCESSED_DIR, filename)
    if not os.path.exists(filepath):
        raise HTTPException(status_code=404, detail=f"File not found: {filename}")

    template_path = os.path.join(os.path.dirname(__file__), "templates", "player.html")
    with open(template_path, "r") as f:
        html = f.read()

    video_url = f"{Config.SERVER_PUBLIC_URL}/video/{filename}"
    html = html.replace("{{VIDEO_URL}}", video_url)
    html = html.replace("{{FILENAME}}", filename)

    return HTMLResponse(content=html)


@app.get("/video/{filename}")
async def video(filename: str, request: Request):
    """Stream a video file with Range request support."""
    filepath = os.path.join(Config.PROCESSED_DIR, filename)
    if not os.path.exists(filepath):
        raise HTTPException(status_code=404, detail=f"File not found: {filename}")

    file_size = os.path.getsize(filepath)
    content_type = mimetypes.guess_type(filename)[0] or "video/mp4"

    range_header = request.headers.get("range")

    if range_header:
        range_spec = range_header.replace("bytes=", "")
        parts = range_spec.split("-")
        start = int(parts[0]) if parts[0] else 0
        end = int(parts[1]) if parts[1] else file_size - 1
        end = min(end, file_size - 1)
        content_length = end - start + 1

        def iter_range():
            with open(filepath, "rb") as f:
                f.seek(start)
                remaining = content_length
                while remaining > 0:
                    chunk_size = min(65536, remaining)
                    chunk = f.read(chunk_size)
                    if not chunk:
                        break
                    remaining -= len(chunk)
                    yield chunk

        return StreamingResponse(
            iter_range(),
            status_code=206,
            headers={
                "Content-Type": content_type,
                "Content-Length": str(content_length),
                "Content-Range": f"bytes {start}-{end}/{file_size}",
                "Accept-Ranges": "bytes",
            },
        )
    else:
        def iter_file():
            with open(filepath, "rb") as f:
                while True:
                    chunk = f.read(65536)
                    if not chunk:
                        break
                    yield chunk

        return StreamingResponse(
            iter_file(),
            headers={
                "Content-Type": content_type,
                "Content-Length": str(file_size),
                "Accept-Ranges": "bytes",
            },
        )
