"""Fetch one public media item using yt-dlp's extractors and HTTP downloader."""
import json
from pathlib import Path
from urllib.parse import urlsplit

from yt_dlp import YoutubeDL
from yt_dlp.downloader.http import HttpFD
from yt_dlp.utils import DownloadError


class MediaError(Exception):
    pass


class QuietLogger:
    # Extractor diagnostics may contain signed URLs or cookies.
    def debug(self, message): pass
    def warning(self, message): pass
    def error(self, message): pass


def download(link, directory, progress):
    """Return a title and local filenames. The caller owns the temporary directory."""
    direct = "[protocol~='^https?$']"
    formats = "/".join((
        f"b[ext=mp4]{direct}",
        f"bv[ext=mp4]{direct}+ba[ext=m4a]{direct}",
        f"b{direct}", f"ba{direct}", f"bv{direct}",
    ))
    options = {
        "format": formats, "noplaylist": True, "playlistend": 1,
        "extract_flat": "in_playlist", "cachedir": False, "socket_timeout": 20,
        "retries": 2, "js_runtimes": {}, "quiet": True, "noprogress": True,
        "logger": QuietLogger(),
    }

    def update(percent):
        if not progress(percent):
            raise MediaError("cancelled")

    update(-1)
    with YoutubeDL(options) as engine:
        info = engine.extract_info(link, download=False)
        if not info or info.get("_type", "video") != "video" or info.get("is_live"):
            raise MediaError("single")
        tracks = info.get("requested_formats") or [info]
        files = []
        for index, track in enumerate(tracks):
            url = track.get("url", "")
            if urlsplit(url).scheme not in ("http", "https") or track.get("has_drm"):
                raise MediaError("format")
            ext = track.get("ext", "")
            if not (ext.isascii() and ext.isalnum() and len(ext) <= 8):
                raise MediaError("format")
            path = Path(directory) / f"track{index}.{ext}"

            def hook(status):
                total = status.get("total_bytes") or status.get("total_bytes_estimate")
                percent = min(1, status.get("downloaded_bytes", 0) / total) if total else None
                update(int(100 * (index + percent) / len(tracks)) if percent is not None else -1)

            update(int(100 * index / len(tracks)))
            downloader = HttpFD(engine, options)
            downloader.add_progress_hook(hook)
            ok, _ = downloader.download(str(path), {**info, **track})
            if not ok or not path.is_file() or not path.stat().st_size:
                raise MediaError("download")
            files.append(path.name)
        return {"title": info.get("title") or "ClipDock", "files": files}


def run(link, directory, progress):
    try:
        return json.dumps(download(link, directory, progress))
    except MediaError as error:
        return json.dumps({"error": str(error)})
    except (DownloadError, OSError):
        return '{"error":"download"}'
