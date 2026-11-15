(() => {
  "use strict";
  // XML/SVG documents cannot host the overlay element and have no <video> to save.
  if (!(document.documentElement instanceof HTMLElement)) return;
  const { isRestrictedUrl } = globalThis.ClipDockSitePolicy;
  if (isRestrictedUrl(location.href)) return;
  const videos = new Set();
  const protectedVideos = new WeakSet();
  const host = document.createElement("clipdock-controls");
  const shadow = host.attachShadow({ mode: "closed" });
  const style = document.createElement("style");
  style.textContent = `:host{all:initial!important;position:fixed!important;z-index:2147483647!important;display:none;pointer-events:none!important}button{all:initial;box-sizing:border-box;cursor:pointer;pointer-events:auto;background:#a5e7ba;color:#11281b;border:1px solid #87cfa2;border-radius:7px;padding:10px 14px;font:600 12px/1.4 system-ui,sans-serif;box-shadow:0 3px 16px #0005;white-space:nowrap}button:hover{background:#c0f2d0}button:focus-visible{outline:3px solid white;outline-offset:2px}button:disabled{cursor:default;opacity:.85}`;
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = "↓ MP4で保存";
  shadow.append(style, button);
  const STALE = "拡張を再読み込みした場合は、このページも再読み込みしてください。";
  let current = null;
  let hideTimer;
  let reportTimer = 0;
  let scanTimer = 0;
  let observer = null;
  let retired = false;
  let overlayEnabled = true;
  let busy = false;
  let sentSignature = "";
  const isWeb = url => { try { return ["http:", "https:"].includes(new URL(url).protocol); } catch { return false; } };
  // Once the extension is reloaded or removed, chrome.runtime throws synchronously in this orphaned script.
  async function send(message) {
    try { return (await chrome.runtime.sendMessage(message)) || { ok: false, error: STALE }; }
    catch { if (!chrome.runtime?.id) retire(); return { ok: false, error: STALE }; }
  }
  // Stops background discovery for good; the button stays so a click can still explain what to do.
  function retire() {
    retired = true;
    observer?.disconnect();
    clearTimeout(scanTimer); clearTimeout(reportTimer);
  }
  chrome.storage.local.get({ overlay: true }).then(s => { overlayEnabled = s.overlay; }).catch(() => {});
  chrome.storage.onChanged.addListener((changes, area) => { if (area === "local" && changes.overlay) { overlayEnabled = changes.overlay.newValue; if (!overlayEnabled) hide(); } });

  function hide() { if (!busy) host.style.setProperty("display", "none", "important"); }
  function place() {
    if (!current?.isConnected || !overlayEnabled || isRestrictedUrl(current.currentSrc || current.src)) return hide();
    const rect = current.getBoundingClientRect();
    if (rect.width < 160 || rect.height < 80 || rect.bottom < 0 || rect.top > innerHeight || document.fullscreenElement === current) return hide();
    const parent = document.fullscreenElement || document.documentElement;
    if (host.parentNode !== parent) parent.append(host);
    host.style.setProperty("top", `${Math.max(8, rect.top + 12)}px`, "important");
    host.style.setProperty("left", `${Math.max(8, Math.min(innerWidth - 156, rect.right - 150))}px`, "important");
    host.style.setProperty("display", "block", "important");
  }
  document.addEventListener("pointerover", event => {
    const video = event.composedPath().find(el => el instanceof HTMLVideoElement);
    if (!video || busy) return;
    current = video;
    button.disabled = protectedVideos.has(video) || !!video.mediaKeys;
    button.textContent = button.disabled ? "保護された動画" : "↓ MP4で保存";
    button.title = button.disabled ? "DRM保護された動画には対応していません" : "MP4としてダウンロード";
    clearTimeout(hideTimer); place();
  }, true);
  document.addEventListener("pointerout", event => { if (event.target === current) hideTimer = setTimeout(hide, 1800); }, true);
  host.addEventListener("pointerenter", () => clearTimeout(hideTimer));
  host.addEventListener("pointerleave", () => { hideTimer = setTimeout(hide, 1000); });
  addEventListener("scroll", () => { if (host.style.display !== "none") place(); }, { passive: true, capture: true });
  addEventListener("resize", () => { if (host.style.display !== "none") place(); });
  document.addEventListener("fullscreenchange", hide);
  button.addEventListener("click", async event => {
    if (!event.isTrusted || !current || busy || current.mediaKeys || protectedVideos.has(current) ||
        isRestrictedUrl(location.href) || isRestrictedUrl(current.currentSrc || current.src)) return;
    event.preventDefault(); event.stopPropagation();
    busy = true; button.disabled = true; button.textContent = "送信中…";
    const src = current.currentSrc || current.src;
    const result = await send({ type: "download", url: isWeb(src) ? src : location.href });
    button.textContent = result.ok ? "✓ ClipDockに追加" : "接続を確認してください";
    button.title = result.ok ? "ClipDockで進捗を確認できます" : result.error || STALE;
    busy = false;
    hideTimer = setTimeout(() => { button.disabled = false; hide(); }, result.ok ? 2400 : 7000);
  });

  function discover() {
    for (const video of document.querySelectorAll("video")) {
      if (videos.has(video)) continue;
      videos.add(video);
      for (const event of ["loadedmetadata", "play", "emptied"]) video.addEventListener(event, scheduleReport);
      video.addEventListener("encrypted", () => { protectedVideos.add(video); if (current === video) { button.disabled = true; button.textContent = "保護された動画"; } });
    }
    for (const video of videos) if (!video.isConnected) videos.delete(video);
    scheduleReport();
  }
  // Both timers run once per interval instead of restarting on every event: pages that mutate
  // the DOM continuously (tickers, chat, progress text) would otherwise postpone the scan forever.
  function scheduleScan() { if (!scanTimer && !retired) scanTimer = setTimeout(() => { scanTimer = 0; discover(); }, 250); }
  function scheduleReport() { if (!reportTimer && !retired) reportTimer = setTimeout(() => { reportTimer = 0; void report(); }, 350); }
  async function report(force = false) {
    const candidates = [];
    let index = 0;
    for (const video of videos) {
      index++;
      if (protectedVideos.has(video) || video.mediaKeys) continue;
      const sources = [video.currentSrc, video.src, ...[...video.querySelectorAll("source")].map(s => s.src)];
      for (const url of new Set(sources.filter(url => isWeb(url) && !isRestrictedUrl(url)))) candidates.push({ url, kind: /\.m3u8(?:[?#]|$)/i.test(url) ? "HLS" : /\.mpd(?:[?#]|$)/i.test(url) ? "DASH" : /\.mp4(?:[?#]|$)/i.test(url) ? "MP4" : "VIDEO", title: `${document.title || "動画"} · ${index}` });
    }
    const signature = JSON.stringify(candidates);
    if (!force && signature === sentSignature) return;
    sentSignature = signature;
    await send({ type: "discovered", candidates });
  }
  // Appending the overlay itself is reported with its parent as the target, so compare the moved nodes.
  const ownMutation = record => record.type === "childList" && [...record.addedNodes, ...record.removedNodes].every(node => node === host);
  observer = new MutationObserver(records => { if (!records.every(ownMutation)) scheduleScan(); });
  observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["src"] });
  // A page restored from the back/forward cache keeps this script but the background list was cleared.
  addEventListener("pageshow", event => { if (event.persisted) { sentSignature = ""; scheduleReport(); } });
  chrome.runtime.onMessage.addListener((message, _sender, respond) => {
    if (message?.type !== "scan") return;
    discover(); report(true).then(() => respond({ ok: true }));
    return true;
  });
  discover();
})();
