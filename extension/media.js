import "./distribution.js";
import "./site-policy.js";

export const { storeBuild, isRestrictedUrl, restrictionMessage } = globalThis.ClipDockSitePolicy;
export const HOST = "com.clipdock.downloader";
export const QUALITIES = new Set(["best", "2160", "1080", "720", "480"]);

export function isWebUrl(value) {
  if (typeof value !== "string" || value.length > 16384 || /[\u0000-\u001f\u007f]/.test(value)) return false;
  try { const u = new URL(value); return ["http:", "https:"].includes(u.protocol) && !!u.hostname && !u.username && !u.password; }
  catch { return false; }
}

export function classifyMedia(url, contentType = "") {
  if (!isWebUrl(url) || isRestrictedUrl(url)) return null;
  const u = new URL(url);
  const path = u.pathname.toLowerCase();
  const mime = contentType.toLowerCase().split(";")[0].trim();
  // Fragments are not standalone videos. Keep the playlist URL instead.
  if (/\.(ts|m4s|cmfv|cmfa|m4a|aac|mp3|vtt|srt)$/.test(path) || u.searchParams.has("range")) return null;
  if (path.endsWith(".m3u8") || ["application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl"].includes(mime)) return "HLS";
  if (path.endsWith(".mpd") || mime === "application/dash+xml") return "DASH";
  if (path.endsWith(".mp4") || mime === "video/mp4") return "MP4";
  if (/\.(webm|mov|mkv|flv|avi|m4v)$/.test(path) || mime.startsWith("video/") && mime !== "video/mp2t") return "VIDEO";
  return null;
}

export function mergeCandidates(existing, incoming) {
  const result = existing.filter(item => !isRestrictedUrl(item.url) && !isRestrictedUrl(item.referer));
  for (const candidate of incoming) {
    if (!isWebUrl(candidate?.url) || isRestrictedUrl(candidate.url) || isRestrictedUrl(candidate.referer)) continue;
    const clean = {
      url: candidate.url,
      kind: ["HLS", "DASH", "MP4", "VIDEO"].includes(candidate.kind) ? candidate.kind : "VIDEO",
      title: String(candidate.title || "動画").replace(/[\u0000-\u001f]/g, "").slice(0, 180),
      referer: isWebUrl(candidate.referer) ? candidate.referer : "",
      size: Number.isFinite(candidate.size) && candidate.size > 0 ? candidate.size : 0,
    };
    const index = result.findIndex(item => item.url === clean.url);
    if (index >= 0) result[index] = { ...result[index], ...clean, size: clean.size || result[index].size };
    else result.push(clean);
  }
  return result.slice(-50);
}

export function mediaTitle(url) {
  try { const u = new URL(url); return decodeURIComponent(u.pathname.split("/").pop() || u.hostname).slice(0, 120); }
  catch { return "動画"; }
}
