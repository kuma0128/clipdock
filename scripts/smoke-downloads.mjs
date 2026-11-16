// Optional real-engine integration test. Only synthetic media is used;
// app settings and history are never opened. Requires a previously built test project.
import assert from "node:assert/strict";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { spawn, spawnSync } from "node:child_process";

const tools = path.resolve(process.argv[2] || "artifacts/ClipDock-win-x64/tools");
const artifacts = path.resolve("artifacts"); fs.mkdirSync(artifacts, { recursive: true });
const root = fs.mkdtempSync(path.join(artifacts, "parallel-smoke-"));
const fixture = path.join(root, "fixture"); fs.mkdirSync(fixture);
function ffmpeg(args) {
  const result = spawnSync(path.join(tools, "ffmpeg.exe"), ["-hide_banner", "-loglevel", "error", "-y", ...args], { cwd: fixture, windowsHide: true, timeout: 30000 });
  assert.equal(result.status, 0, String(result.error || result.stderr));
}
ffmpeg(["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "2", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", path.join(fixture, "sample.mp4")]);
ffmpeg(["-i", path.join(fixture, "sample.mp4"), "-c:v", "libvpx-vp9", "-threads", "2", "-c:a", "libopus", path.join(fixture, "sample.webm")]);
ffmpeg(["-i", path.join(fixture, "sample.mp4"), "-c", "copy", "-hls_time", "1", "-hls_list_size", "0", path.join(fixture, "sample.m3u8")]);
ffmpeg(["-i", path.join(fixture, "sample.mp4"), "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-f", "dash", "-seg_duration", "1", "-adaptation_sets", "id=0,streams=v id=1,streams=a", path.join(fixture, "sample.mpd")]);

const active = new Map(); let maxConcurrentJobs = 0;
const server = http.createServer((req, res) => {
  const parts = new URL(req.url, "http://localhost").pathname.split("/").filter(Boolean);
  const [job, name] = parts;
  if (parts.length !== 2 || !/^[a-zA-Z0-9_.-]+$/.test(name) || !fs.existsSync(path.join(fixture, name))) {
    res.writeHead(404); res.end("Not found"); return;
  }
  const data = fs.readFileSync(path.join(fixture, name));
  const mime = name.endsWith(".mpd") ? "application/dash+xml" : name.endsWith(".m3u8") ? "application/vnd.apple.mpegurl" : name.endsWith(".ts") ? "video/mp2t" : name.endsWith(".webm") ? "video/webm" : "video/mp4";
  const range = req.headers.range?.match(/^bytes=(\d+)-(\d*)$/);
  let offset = range ? Number(range[1]) : 0;
  const end = range?.[2] ? Math.min(Number(range[2]), data.length - 1) : data.length - 1;
  if (offset > end) { res.writeHead(416); res.end(); return; }
  res.writeHead(range ? 206 : 200, { "Content-Type": mime, "Content-Length": end - offset + 1, "Accept-Ranges": "bytes",
    ...(range ? { "Content-Range": `bytes ${offset}-${end}/${data.length}` } : {}) });
  if (req.method === "HEAD") { res.end(); return; }
  active.set(job, (active.get(job) || 0) + 1);
  maxConcurrentJobs = Math.max(maxConcurrentJobs, active.size);
  const timer = setInterval(() => {
    const next = Math.min(offset + (job.startsWith("slow-") ? 512 : 2048), end + 1);
    res.write(data.subarray(offset, next)); offset = next;
    if (offset > end) { clearInterval(timer); res.end(); }
  }, job.startsWith("slow-") ? 80 : 40);
  res.on("close", () => {
    clearInterval(timer);
    const count = active.get(job) - 1;
    if (count) active.set(job, count); else active.delete(job);
  });
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
try {
  const address = `http://127.0.0.1:${server.address().port}`;
  const executable = path.resolve("tests/ClipDock.Checks/bin/Release/net10.0/ClipDock.Checks.exe");
  const child = spawn(executable, ["--download-smoke", tools, address, path.join(root, "保存先 with spaces")], { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  child.stdout.pipe(process.stdout); child.stderr.pipe(process.stderr);
  const status = await new Promise((resolve, reject) => { child.once("error", reject); child.once("exit", resolve); });
  assert.equal(status, 0, "Real-engine checks failed");
  assert.equal(maxConcurrentJobs, 2, "The HTTP server must observe two jobs transferring at once and never three");
  console.log(`PASS observed ${maxConcurrentJobs} simultaneous transfers; outputs: ${root}`);
} finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
