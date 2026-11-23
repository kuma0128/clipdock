import assert from "node:assert/strict";
import test from "node:test";
import { onRequest } from "../functions/downloads/[[file]].js";

const file = "ClipDock-1.0.3-win-x64-unsigned-setup.exe";
const uploaded = new Date("2026-11-15T01:02:03.456Z");
const bytes = new TextEncoder().encode("0123456789");

function fixture({ missing = false, changed = false } = {}) {
  const calls = [];
  const object = { size: bytes.length, uploaded, etag: "release-etag", httpEtag: '"release-etag"' };
  const bucket = {
    async head(key) { calls.push(["head", key]); return missing ? null : object; },
    async get(key, options) {
      calls.push(["get", key, options]);
      assert.deepEqual(options.onlyIf, { etagMatches: "release-etag" });
      if (changed) return object;
      const range = options.range;
      return { ...object, body: new Blob([range ? bytes.slice(range.offset, range.offset + range.length) : bytes]).stream() };
    }
  };
  return {
    calls,
    request(headers = {}, method = "GET", path = file) {
      return onRequest({ request: new Request(`https://clipdock.pages.dev/downloads/${path}`, { method, headers }), env: { CLIPDOCK_RELEASES: bucket } });
    }
  };
}

test("only release filenames can read the bucket, and only GET/HEAD are allowed", async () => {
  const f = fixture();
  for (const path of ["", "private.txt", `${file}/extra`, `nested/${file}`, `%2F${file}`, `${file}%00`]) {
    assert.equal((await f.request({}, "GET", path)).status, 404);
  }
  for (const method of ["POST", "PUT", "DELETE", "OPTIONS"]) {
    const response = await f.request({}, method);
    assert.equal(response.status, 405);
    assert.equal(response.headers.get("Allow"), "GET, HEAD");
  }
  assert.deepEqual(f.calls, []);
});

test("macOS disk images and their checksums are release files from 1.0.3 on", async () => {
  const f = fixture();
  for (const version of ["1.0.3", "1.0.4", "1.0.5", "1.0.6"]) for (const arch of ["arm64", "x64"]) {
    const image = `ClipDock-${version}-mac-${arch}-unsigned.dmg`;
    const response = await f.request({}, "GET", image);
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("Content-Type"), "application/octet-stream");
    assert.equal(response.headers.get("Content-Disposition"), `attachment; filename="${image}"`);
    assert.equal((await f.request({}, "GET", `${image}.sha256`)).headers.get("Content-Type"), "text/plain; charset=utf-8");
  }
  const before = f.calls.length;
  for (const path of ["ClipDock-1.0.2-mac-arm64-unsigned.dmg", "ClipDock-1.0.3-mac-universal-unsigned.dmg", "ClipDock-1.0.3-mac-arm64.dmg"])
    assert.equal((await f.request({}, "GET", path)).status, 404);
  assert.equal(f.calls.length, before);
});

test("streams the installer and checksum with download metadata", async () => {
  const f = fixture();
  const response = await f.request();
  assert.equal(response.status, 200);
  assert.equal(await response.text(), "0123456789");
  assert.equal(response.headers.get("Content-Type"), "application/octet-stream");
  assert.equal(response.headers.get("Content-Length"), "10");
  assert.equal(response.headers.get("ETag"), '"release-etag"');
  assert.equal(response.headers.get("Last-Modified"), uploaded.toUTCString());
  assert.equal(response.headers.get("Content-Disposition"), `attachment; filename="${file}"`);
  assert.equal(response.headers.get("Accept-Ranges"), "bytes");
  assert.equal(response.headers.get("X-Content-Type-Options"), "nosniff");
  assert.equal(response.headers.get("Cache-Control"), "public, max-age=86400");
  const checksum = await f.request({}, "GET", `${file}.sha256`);
  assert.equal(checksum.headers.get("Content-Type"), "text/plain; charset=utf-8");
  assert.equal(f.calls.at(-1)[1], `${file}.sha256`);
});

test("HEAD and unchanged requests never fetch a body", async () => {
  const f = fixture();
  const head = await f.request({ Range: "bytes=1-2" }, "HEAD");
  assert.equal(head.status, 200);
  assert.equal(head.headers.get("Content-Length"), "10");
  assert.equal(head.body, null);
  for (const headers of [
    { "If-None-Match": 'W/"release-etag"' },
    { "If-None-Match": '"other", "release-etag"' },
    { "If-None-Match": "*" },
    { "If-Modified-Since": uploaded.toUTCString() }
  ]) {
    const response = await f.request(headers);
    assert.equal(response.status, 304);
    assert.equal(response.body, null);
    assert.equal(response.headers.get("ETag"), '"release-etag"');
  }
  assert(f.calls.every(call => call[0] === "head"));
});

test("HTTP preconditions honor ETag precedence and strong If-Match", async () => {
  const f = fixture();
  for (const headers of [
    { "If-Match": '"other"', "If-None-Match": "*" },
    { "If-Match": 'W/"release-etag"' },
    { "If-Unmodified-Since": "Sat, 14 Nov 2026 00:00:00 GMT" }
  ]) assert.equal((await f.request(headers)).status, 412);
  assert(f.calls.every(call => call[0] === "head"));
  for (const headers of [
    { "If-Match": '"release-etag"', "If-Unmodified-Since": "Sat, 14 Nov 2026 00:00:00 GMT" },
    { "If-None-Match": '"other"', "If-Modified-Since": "Mon, 16 Nov 2026 00:00:00 GMT" },
    { "If-Modified-Since": "invalid" }
  ]) assert.equal((await f.request(headers)).status, 200);
});

test("single closed, open, and suffix ranges return exact bytes", async () => {
  for (const [range, body, contentRange] of [
    ["bytes=2-4", "234", "bytes 2-4/10"],
    ["bytes=8-", "89", "bytes 8-9/10"],
    ["bytes=-3", "789", "bytes 7-9/10"],
    ["bytes=8-999999999999999999999", "89", "bytes 8-9/10"],
    ["bytes=-999999999999999999999", "0123456789", "bytes 0-9/10"]
  ]) {
    const response = await fixture().request({ Range: range });
    assert.equal(response.status, 206);
    assert.equal(await response.text(), body);
    assert.equal(response.headers.get("Content-Length"), String(body.length));
    assert.equal(response.headers.get("Content-Range"), contentRange);
  }
});

test("unsatisfiable ranges return 416 without fetching data", async () => {
  const f = fixture();
  for (const range of ["bytes=10-", "bytes=5-2", "bytes=-0", "bytes=999999999999999999999-"]) {
    const response = await f.request({ Range: range });
    assert.equal(response.status, 416);
    assert.equal(response.headers.get("Content-Range"), "bytes */10");
  }
  assert(f.calls.every(call => call[0] === "head"));
});

test("unsupported ranges and stale If-Range return the whole file", async () => {
  for (const headers of [
    { Range: "bytes=0-1,4-5" }, { Range: "invalid" }, { Range: "items=0-1" },
    { Range: "bytes=2-4", "If-Range": '"other"' },
    { Range: "bytes=2-4", "If-Range": 'W/"release-etag"' },
    { Range: "bytes=2-4", "If-Range": "Mon, 16 Nov 2026 00:00:00 GMT" }
  ]) {
    const response = await fixture().request(headers);
    assert.equal(response.status, 200);
    assert.equal(await response.text(), "0123456789");
  }
  for (const ifRange of ['"release-etag"', uploaded.toUTCString()]) {
    assert.equal((await fixture().request({ Range: "bytes=2-4", "If-Range": ifRange })).status, 206);
  }
});

test("missing binding, missing objects, and concurrent replacements fail closed", async () => {
  const unbound = await onRequest({ request: new Request(`https://clipdock.pages.dev/downloads/${file}`), env: {} });
  assert.equal(unbound.status, 503);
  assert.equal(unbound.headers.get("Cache-Control"), "no-store");
  assert.equal((await fixture({ missing: true }).request()).status, 404);
  const changed = await fixture({ changed: true }).request({ Range: "bytes=2-4" });
  assert.equal(changed.status, 503);
  assert.equal(changed.body, null);
  assert.equal(changed.headers.get("Cache-Control"), "no-store");
});
