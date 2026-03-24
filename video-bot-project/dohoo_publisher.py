"""Dohoo.ai YouTube publishing integration."""

import logging
import aiohttp

from config import Config

logger = logging.getLogger(__name__)


async def get_youtube_connections() -> list[dict]:
    """Fetch available YouTube channel connections from dohoo.ai."""
    url = f"{Config.DOHOO_API_URL}/connections/unified"
    headers = {"X-API-Key": Config.DOHOO_API_KEY}

    async with aiohttp.ClientSession() as session:
        async with session.get(url, headers=headers) as resp:
            if resp.status != 200:
                body = await resp.text()
                logger.error(f"Dohoo connections error {resp.status}: {body[:200]}")
                raise Exception(f"Dohoo API error: {resp.status}")

            data = await resp.json()
            connections = [
                c for c in data.get("connections", [])
                if c.get("platform") == "youtube"
            ]
            logger.info(f"Found {len(connections)} YouTube connections")
            return connections


async def publish_to_youtube(
    file_url: str,
    connection_id: str,
    title: str,
    description: str = "",
    caption: str = "",
) -> dict:
    """Publish a video to YouTube via dohoo.ai API."""
    url = f"{Config.DOHOO_API_URL}/v2/youtube/publish"
    headers = {"X-API-Key": Config.DOHOO_API_KEY}

    payload = {
        "connectionId": connection_id,
        "fileUrl": file_url,
        "title": title,
        "description": description,
        "caption": caption,
        "visibility": "unlisted",
    }

    async with aiohttp.ClientSession() as session:
        async with session.post(url, headers=headers, json=payload) as resp:
            data = await resp.json()

            if resp.status != 200 or not data.get("success"):
                error_msg = data.get("error", f"HTTP {resp.status}")
                logger.error(f"Dohoo publish failed ({resp.status}): {error_msg}")
                raise Exception(f"Dohoo publish failed ({resp.status}): {error_msg}")

            logger.info(f"Published to YouTube: {title}")
            return data
