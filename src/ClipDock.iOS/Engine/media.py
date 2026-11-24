"""Fetch one public media item with the downloaders available on the device."""
import json
from pathlib import Path
from urllib.parse import urlsplit

from yt_dlp import YoutubeDL
from yt_dlp.downloader.dash import DashSegmentsFD
from yt_dlp.downloader.hls import HlsFD
from yt_dlp.downloader.http import HttpFD
from yt_dlp.networking.exceptions import RequestError
from yt_dlp.utils import DownloadError


class MediaError(Exception):
    pass


class QuietLogger:
    # Extractor diagnostics may contain signed URLs or cookies.
    def debug(self, message): pass
    def warning(self, message): pass
    def error(self, message): pass


def download(link, directory, progress, *, segmented=False):
    """Return a title and local filenames. The caller owns the temporary directory."""
    protocols = {"http": HttpFD, "https": HttpFD}
    if segmented:
        protocols.update(m3u8=HlsFD, m3u8_native=HlsFD, http_dash_segments=DashSegmentsFD)
    supported = "[protocol~='^(" + "|".join(protocols) + ")$']"
    combined = f"b[ext=mp4]{supported}"
    separate = f"bv[ext=mp4]{supported}+ba[ext=m4a]{supported}"
    formats = "/".join((
        *((separate, combined) if segmented else (combined, separate)),
        f"b{supported}", f"ba{supported}", f"bv{supported}",
    ))
    options = {
        "format": formats, "noplaylist": True, "playlistend": 1,
        "extract_flat": "in_playlist", "cachedir": False, "socket_timeout": 20,
        "retries": 2, "js_runtimes": {}, "quiet": True, "noprogress": True,
        "logger": QuietLogger(), "skip_unavailable_fragments": False,
        "fragment_retries": 2,
    }

    def update(percent):
        if not progress(percent):
            raise MediaError("cancelled")

    update(-1)
    with YoutubeDL(options) as engine:
        info = engine.extract_info(link, download=False)
        if (segmented and info and info.get("_type") in ("multi_video", "playlist")
                and info.get("extractor_key") in ("Twitter", "Generic")):
            entry = next(iter(info.get("entries") or []), None)
            info = engine.process_ie_result(entry, download=False) if entry else None
        if not info or info.get("_type", "video") != "video" or info.get("is_live"):
            raise MediaError("single")
        tracks = info.get("requested_formats") or [info]
        if len(tracks) == 1:
            # Preserve both kinds of media when the source offers both.
            for codec in ("vcodec", "acodec"):
                if info.get(codec) == "none" and any(
                    f.get(codec) not in (None, "none") and f.get("ext") != "mhtml"
                    for f in info.get("formats", [])
                ):
                    raise MediaError("format")
        files = []
        remux = len(tracks) == 2
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
            descriptor = {**info, **track}
            descriptor.pop("requested_formats", None)
            downloader_type = protocols.get(track.get("protocol"))
            if not downloader_type:
                raise MediaError("format")
            downloader = downloader_type(engine, options)
            if downloader_type is HlsFD:
                manifest = descriptor.get("hls_media_playlist_data")
                if not manifest:
                    with engine.urlopen(downloader._prepare_url(descriptor, url)) as response:
                        descriptor["url"] = response.url
                        manifest = response.read().decode("utf-8-sig")
                if "#EXT-X-ENDLIST" not in {line.strip() for line in manifest.splitlines()}:
                    raise MediaError("single")
                if not HlsFD.can_download(manifest, descriptor):
                    raise MediaError("format")
                descriptor["hls_media_playlist_data"] = manifest
            remux |= downloader_type is not HttpFD
            downloader.add_progress_hook(hook)
            ok, _ = downloader.download(str(path), descriptor)
            if not ok or not path.is_file() or not path.stat().st_size:
                raise MediaError("download")
            files.append(path.name)
        return {"title": info.get("title") or "ClipDock", "files": files, "remux": remux}


def run(link, directory, progress, *, segmented=False):
    try:
        return json.dumps(download(link, directory, progress, segmented=segmented))
    except MediaError as error:
        return json.dumps({"error": str(error)})
    except (DownloadError, RequestError, OSError):
        return '{"error":"download"}'
