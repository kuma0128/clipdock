import { isWebUrl } from "./media.js";

const $ = id => document.getElementById(id);
let activeTab;
let busy = false;
const request = message => chrome.runtime.sendMessage(message);
function notify(message, error = false) { $("notice").textContent = message; $("notice").classList.toggle("error", error); $("notice").hidden = false; }
function render(candidates) {
  $("count").textContent = String(candidates.length);
  $("empty").hidden = candidates.length > 0;
  $("candidates").replaceChildren();
  for (const item of candidates) {
    const row = document.createElement("div"); row.className = "candidate";
    const title = document.createElement("div"); title.className = "candidate-title"; title.textContent = item.title; title.title = item.title;
    const meta = document.createElement("div"); meta.className = "candidate-meta";
    meta.textContent = `${item.kind} · ${new URL(item.url).hostname}${item.size ? ` · ${(item.size / 1048576).toFixed(1)} MB` : ""}`;
    const button = document.createElement("button"); button.textContent = "保存"; button.disabled = busy;
    button.setAttribute("aria-label", `${item.title} を保存`);
    button.addEventListener("click", () => save(item.url, item.referer));
    row.append(title, meta, button); $("candidates").append(row);
  }
}
async function refresh(scan = false) {
  if (!activeTab?.id) return;
  if (scan) await chrome.tabs.sendMessage(activeTab.id, { type: "scan" }).catch(() => {});
  const result = await request({ type: "snapshot", tabId: activeTab.id });
  if (!result.ok) throw new Error(result.error);
  activeTab = result.tab;
  $("page-title").textContent = activeTab.title || "動画ページを開いてください";
  $("page-host").textContent = isWebUrl(activeTab.url) ? new URL(activeTab.url).hostname : "Chromeの設定画面などでは利用できません";
  $("save-page").disabled = busy || !isWebUrl(activeTab.url);
  render(result.candidates);
}
async function save(url, referer) {
  if (busy || !activeTab) return;
  busy = true;
  $("save-page").disabled = true;
  document.querySelectorAll(".candidate button").forEach(b => { b.disabled = true; });
  try {
    const result = await request({ type: "download", tabId: activeTab.id, pageUrl: activeTab.url, url, referer,
      quality: $("quality").value, useCookies: $("cookies").checked });
    if (!result.ok) throw new Error(result.error);
    notify("✓ ClipDockに追加しました。進捗はアプリで確認できます。");
  } catch (error) { notify(error.message, true); }
  finally { busy = false; await refresh().catch(() => {}); }
}
$("save-page").addEventListener("click", () => save(activeTab?.url, activeTab?.url));
$("refresh").addEventListener("click", () => refresh(true).catch(error => notify(error.message, true)));
$("quality").addEventListener("change", () => chrome.storage.local.set({ quality: $("quality").value }));
$("overlay").addEventListener("change", () => chrome.storage.local.set({ overlay: $("overlay").checked }));
$("cookies").addEventListener("change", async () => {
  if (!$("cookies").checked) return;
  try {
    const granted = await chrome.permissions.request({ permissions: ["cookies"] });
    if (!granted) { $("cookies").checked = false; notify("ログイン情報を使わずに保存します。"); }
  } catch (error) { $("cookies").checked = false; notify(error.message, true); }
});
$("open-app").addEventListener("click", async () => {
  try { const result = await request({ type: "open" }); if (!result.ok) throw new Error(result.error); notify("ClipDockを開きました。"); }
  catch (error) { notify(error.message, true); }
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "session" && activeTab && changes[`tab-${activeTab.id}`]) void refresh().catch(() => {});
});

async function init() {
  const settings = await chrome.storage.local.get({ quality: "1080", overlay: true });
  $("quality").value = settings.quality;
  $("overlay").checked = settings.overlay;
  [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true });
  await refresh(true);
  const health = await request({ type: "ping" });
  $("connection").textContent = health.ok ? health.ready ? "● ClipDockに接続済み" : "● 接続済み · アプリでエンジンを準備してください" : "○ アプリの接続設定が必要です";
  $("connection").classList.toggle("online", health.ok);
  if (!health.ok) $("connection").title = health.error;
}
init().catch(error => { $("connection").textContent = "○ 接続を確認してください"; notify(error.message, true); });
