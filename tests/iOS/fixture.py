"""Serve original video and audio fixtures to the iPhone simulator."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
from pathlib import Path
import math
import struct
import wave

buffer = BytesIO()
with wave.open(buffer, "wb") as sample:
    sample.setparams((1, 2, 8000, 0, "NONE", ""))
    sample.writeframes(b"".join(struct.pack("<h", int(4096 * math.sin(i * math.tau * 440 / 8000)))
                                for i in range(4000)))
page = b'<html><head><title>ClipDock Sample</title></head><body><video src="/sample.mp4" controls></video></body></html>'
manifest = b"""<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT0.6S">
<Period><AdaptationSet mimeType="video/mp4"><Representation id="v" codecs="avc1.64000A" bandwidth="45000">
<BaseURL>picture.mp4</BaseURL></Representation></AdaptationSet>
<AdaptationSet mimeType="audio/mp4"><Representation id="a" codecs="mp4a.40.2" bandwidth="24000">
<BaseURL>sound.m4a</BaseURL></Representation></AdaptationSet></Period></MPD>"""
responses = {
    "/split.mpd": ("application/dash+xml", manifest),
    "/picture.mp4": ("video/mp4", Path(__file__).with_name("picture.mp4").read_bytes()),
    "/sound.m4a": ("audio/mp4", Path(__file__).with_name("sound.m4a").read_bytes()),
    "/sample.mp4": ("video/mp4", Path(__file__).with_name("sample.mp4").read_bytes()),
    "/tone.wav": ("audio/wav", buffer.getvalue()),
    "/watch": ("text/html", page),
    "/empty": ("text/html", b"<html><title>No media</title></html>"),
}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path not in responses:
            self.send_error(404)
            return
        mime, body = responses[self.path]
        self.send_response(200)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 18741), Handler).serve_forever()
