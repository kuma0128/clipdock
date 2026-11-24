"""Serve an original short WAV and an HTML media page to the iPhone simulator."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
import math
import struct
import wave

buffer = BytesIO()
with wave.open(buffer, "wb") as sample:
    sample.setparams((1, 2, 8000, 0, "NONE", ""))
    sample.writeframes(b"".join(struct.pack("<h", int(4096 * math.sin(i * math.tau * 440 / 8000)))
                                for i in range(4000)))
page = b'<html><head><title>ClipDock Sample</title></head><body><audio src="/tone.wav" controls></audio></body></html>'
responses = {
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
