const releases = new Set(["1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6", "1.0.7"].flatMap(version => {
  const installer = `ClipDock-${version}-win-x64-unsigned-setup.exe`;
  // The macOS disk images exist from 1.0.3 on, one per processor architecture.
  const images = version === "1.0.2" ? [] : ["arm64", "x64"].map(arch => `ClipDock-${version}-mac-${arch}-unsigned.dmg`);
  return [installer, ...images].flatMap(file => [file, `${file}.sha256`]);
}));

function empty(status, extra = {}) {
  return new Response(null, { status, headers: { "Cache-Control": "no-store", ...extra } });
}

function matches(value, etag, weak = false) {
  return value.split(",").some(item => {
    const tag = item.trim();
    return tag === "*" || tag === etag || (weak && tag === `W/${etag}`);
  });
}

function conditionStatus(headers, object) {
  const modified = Math.floor(object.uploaded.getTime() / 1000) * 1000;
  const match = headers.get("If-Match");
  if (match !== null && !matches(match, object.httpEtag)) return 412;
  if (match === null && modified > Date.parse(headers.get("If-Unmodified-Since"))) return 412;
  const none = headers.get("If-None-Match");
  if (none !== null && matches(none, object.httpEtag, true)) return 304;
  if (none === null && modified <= Date.parse(headers.get("If-Modified-Since"))) return 304;
  return 200;
}

function readRange(value, size) {
  const match = /^bytes=(\d*)-(\d*)$/i.exec(value || "");
  if (!match || (!match[1] && !match[2])) return null;
  const total = BigInt(size);
  let start;
  let end;
  if (!match[1]) {
    const suffix = BigInt(match[2]);
    if (suffix === 0n || total === 0n) return false;
    start = suffix >= total ? 0n : total - suffix;
    end = total - 1n;
  } else {
    start = BigInt(match[1]);
    end = match[2] ? BigInt(match[2]) : total - 1n;
    if (start >= total || start > end) return false;
    if (end >= total) end = total - 1n;
  }
  return { offset: Number(start), length: Number(end - start + 1n) };
}

export async function onRequest({ request, env }) {
  const path = new URL(request.url).pathname;
  const file = path.startsWith("/downloads/") ? path.slice("/downloads/".length) : "";
  if (!releases.has(file)) return empty(404);
  if (request.method !== "GET" && request.method !== "HEAD") return empty(405, { Allow: "GET, HEAD" });
  if (!env.CLIPDOCK_RELEASES) return empty(503, { "Retry-After": "60" });

  try {
    const bucket = env.CLIPDOCK_RELEASES;
    const object = await bucket.head(file);
    if (!object) return empty(404);
    const headers = new Headers({
      "Content-Type": file.endsWith(".sha256") ? "text/plain; charset=utf-8" : "application/octet-stream",
      "Content-Disposition": `attachment; filename="${file}"`,
      "Cache-Control": "public, max-age=86400",
      "ETag": object.httpEtag,
      "Last-Modified": object.uploaded.toUTCString(),
      "Accept-Ranges": "bytes",
      "X-Content-Type-Options": "nosniff"
    });
    const condition = conditionStatus(request.headers, object);
    if (condition === 412) return empty(412);
    if (condition === 304) return new Response(null, { status: 304, headers });
    if (request.method === "HEAD") {
      headers.set("Content-Length", String(object.size));
      return new Response(null, { headers });
    }

    const ifRange = request.headers.get("If-Range");
    const rangeMatches = !ifRange || ifRange === object.httpEtag ||
      Date.parse(ifRange) === Math.floor(object.uploaded.getTime() / 1000) * 1000;
    const range = rangeMatches ? readRange(request.headers.get("Range"), object.size) : null;
    if (range === false) return empty(416, { "Content-Range": `bytes */${object.size}` });
    const result = await bucket.get(file, { onlyIf: { etagMatches: object.etag }, ...(range ? { range } : {}) });
    if (!result) return empty(404);
    if (!result.body) return empty(503, { "Retry-After": "5" });
    headers.set("Content-Length", String(range ? range.length : object.size));
    if (range) headers.set("Content-Range", `bytes ${range.offset}-${range.offset + range.length - 1}/${object.size}`);
    return new Response(result.body, { status: range ? 206 : 200, headers });
  } catch (error) {
    console.error(JSON.stringify({ event: "release_download_failed", error: error?.name || "Error" }));
    return empty(503, { "Retry-After": "60" });
  }
}
