import { HOST, QUALITIES, isWebUrl, classifyMedia, mergeCandidates, mediaTitle } from "./media.js";

// ClipDock.NativeHost rejects messages above 512 KiB; fail here with a clear reason instead.
const MAX_NATIVE_REQUEST_BYTES = 480 * 1024;
const WEB_PATTERNS = ["http://*/*", "https://*/*"];
const MEDIA_REQUEST_FILTER = { urls: WEB_PATTERNS, types: ["media", "xmlhttprequest", "other", "main_frame", "sub_frame"] };
const locks = new Map();
const requestStarts = new Map();
const keyFor = tabId => `tab-${tabId}`;
const withoutHash = url => String(url || "").split("#")[0];
function serial(tabId, action) {
  const previous = locks.get(tabId) || Promise.resolve();
  const next = previous.catch(() => {}).then(action);
  locks.set(tabId, next);
  next.finally(() => { if (locks.get(tabId) === next) locks.delete(tabId); }).catch(() => {});
  return next;
}
async function getState(tabId) {
  const key = keyFor(tabId);
  return (await chrome.storage.session.get(key))[key] || { candidates: [], since: 0, historySince: 0, pageUrl: "" };
}
async function putState(tabId, state) {
  await chrome.storage.session.set({ [keyFor(tabId)]: state });
  await updateAction(tabId, state);
}
async function updateAction(tabId, state) {
  await chrome.action.setBadgeText({ tabId, text: state.candidates.length ? String(state.candidates.length) : "" }).catch(() => {});
  await chrome.action.setBadgeBackgroundColor({ tabId, color: "#427758" }).catch(() => {});
  await chrome.action.setTitle({ tabId, title: "ClipDockで動画を保存" }).catch(() => {});
}
function reset(details, sameDocument) {
  if (details.frameId !== 0) return;
  void serial(details.tabId, async () => {
    // Frameworks call replaceState without leaving the page (scroll restoration, state-only updates).
    // The playlist was requested only once, so dropping it there would lose it for good:
    // a same-document update counts as navigation only when the URL really changes.
    if (sameDocument && withoutHash((await getState(details.tabId)).pageUrl) === withoutHash(details.url)) return;
    await putState(details.tabId, { candidates: [], since: details.timeStamp,
      historySince: sameDocument ? details.timeStamp : 0, pageUrl: details.url });
  }).catch(() => {});
}
function forget(tabId) {
  for (const [requestId, request] of requestStarts) if (request.tabId === tabId) requestStarts.delete(requestId);
  void serial(tabId, () => chrome.storage.session.remove(keyFor(tabId))).catch(() => {});
}
chrome.webNavigation.onCommitted.addListener(details => reset(details, false));
chrome.webNavigation.onHistoryStateUpdated.addListener(details => reset(details, true));
chrome.tabs.onRemoved.addListener(forget);
chrome.tabs.onReplaced.addListener((_addedTabId, removedTabId) => forget(removedTabId));

chrome.webRequest.onBeforeRequest.addListener(details => {
  if (details.tabId >= 0 && !requestStarts.has(details.requestId))
    requestStarts.set(details.requestId, { tabId: details.tabId, timeStamp: details.timeStamp });
}, MEDIA_REQUEST_FILTER);
const finishRequest = details => requestStarts.delete(details.requestId);
chrome.webRequest.onCompleted.addListener(finishRequest, MEDIA_REQUEST_FILTER);
chrome.webRequest.onErrorOccurred.addListener(finishRequest, MEDIA_REQUEST_FILTER);

chrome.webRequest.onHeadersReceived.addListener(details => {
  if (details.tabId < 0 || details.statusCode < 200 || details.statusCode >= 300) return;
  const header = name => details.responseHeaders?.find(h => h.name.toLowerCase() === name)?.value || "";
  const kind = classifyMedia(details.url, header("content-type"));
  if (!kind) return;
  const startedAt = requestStarts.get(details.requestId)?.timeStamp ?? details.timeStamp;
  void serial(details.tabId, async () => {
    const tab = await chrome.tabs.get(details.tabId);
    if (tab.incognito) return;
    const state = await getState(details.tabId);
    if (startedAt < (state.historySince || 0)) return;
    const frame = await chrome.webNavigation.getFrame({ tabId: details.tabId, frameId: details.frameId }).catch(() => null);
    // A response can arrive after its frame navigated; its timestamp is the response time,
    // so the navigation timestamp alone cannot tell which document initiated it.
    if (details.documentId && (!frame || frame.documentId !== details.documentId)) return;
    if (!details.documentId && details.timeStamp < state.since) return;
    if (details.documentLifecycle && details.documentLifecycle !== "active") return;
    const candidate = { url: details.url, kind, title: mediaTitle(details.url),
      referer: isWebUrl(frame?.url) ? frame.url : tab.url,
      size: details.statusCode === 200 ? Number(header("content-length")) : 0 };
    await putState(details.tabId, { ...state, pageUrl: state.pageUrl || tab.url || "", candidates: mergeCandidates(state.candidates, [candidate]) });
  }).catch(() => {});
}, MEDIA_REQUEST_FILTER, ["responseHeaders"]);

async function native(request) {
  let result;
  // A rejection here is always about reaching the host (not registered, moved, blocked or crashed).
  try { result = await chrome.runtime.sendNativeMessage(HOST, request); }
  catch { throw new Error("ClipDockを起動して「Chrome連携を設定」を押してください。アプリの場所を変えた場合も再設定が必要です。"); }
  if (!result?.ok) throw new Error(result?.error || "ClipDockからの応答がありません。");
  return result;
}

async function cookiesFor(tab, urls) {
  if (!await chrome.permissions.contains({ permissions: ["cookies"] })) throw new Error("ログイン情報の利用を許可してください。");
  const stores = await chrome.cookies.getAllCookieStores();
  const store = stores.find(s => s.tabIds.includes(tab.id));
  if (!store) throw new Error("このタブのCookieを取得できませんでした。");
  const collected = new Map();
  for (const url of new Set(urls.filter(isWebUrl))) {
    for (const c of await chrome.cookies.getAll({ url, storeId: store.id })) {
      if (c.partitionKey) continue;
      collected.set(`${c.domain}\n${c.path}\n${c.name}`, {
        domain: c.domain, path: c.path, name: c.name, value: c.value, secure: c.secure,
        httpOnly: c.httpOnly, hostOnly: c.hostOnly, expirationDate: c.expirationDate ?? null,
      });
    }
  }
  if (collected.size > 500) throw new Error("Cookieの数が多すぎます。ログイン情報を使わずにお試しください。");
  return [...collected.values()];
}

// allowCookies is true only for the popup: a page-side message must never be able to opt in to sending cookies.
async function download(message, sender, { allowCookies = false } = {}) {
  const tab = sender.tab || await chrome.tabs.get(message.tabId);
  if (sender.documentId) {
    const frame = await chrome.webNavigation.getFrame({ tabId: tab.id, frameId: sender.frameId }).catch(() => null);
    if (!frame || frame.documentId !== sender.documentId) throw new Error("ページが移動しました。拡張を開き直してください。");
  }
  if (tab.incognito || !isWebUrl(tab.url)) throw new Error("通常のhttp/httpsページで動画を開いてください。");
  if (message.pageUrl && message.pageUrl !== tab.url) throw new Error("ページが移動しました。拡張を開き直してください。");
  const url = isWebUrl(message.url) ? message.url : tab.url;
  const referer = sender.tab && isWebUrl(sender.url) ? sender.url : isWebUrl(message.referer) ? message.referer : tab.url;
  const settings = await chrome.storage.local.get({ quality: "1080" });
  const quality = message.quality || settings.quality;
  if (!QUALITIES.has(quality)) throw new Error("画質を選び直してください。");
  const cookies = allowCookies && message.useCookies === true ? await cookiesFor(tab, [tab.url, url, referer]) : [];
  const request = { action: "download", url, pageUrl: tab.url, referer, title: tab.title || "動画", quality, userAgent: navigator.userAgent, cookies };
  if (new TextEncoder().encode(JSON.stringify(request)).length > MAX_NATIVE_REQUEST_BYTES)
    throw new Error(cookies.length ? "ログイン情報が大きすぎて送信できません。ログイン情報を使わずにお試しください。" : "URLが長すぎて送信できません。");
  const result = await native(request);
  await serial(tab.id, async () => updateAction(tab.id, await getState(tab.id))).catch(() => {});
  return result;
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id) return false;
  const fromPopup = !sender.tab && sender.url === chrome.runtime.getURL("popup.html");
  (async () => {
    if (message?.type === "discovered" && sender.tab && !sender.tab.incognito && isWebUrl(sender.url)) {
      return serial(sender.tab.id, async () => {
        const frame = await chrome.webNavigation.getFrame({ tabId: sender.tab.id, frameId: sender.frameId });
        if (!frame || frame.documentId !== sender.documentId) return { ok: true };
        const state = await getState(sender.tab.id);
        const incoming = Array.isArray(message.candidates) ? message.candidates.slice(0, 50).map(c => ({ ...c, referer: sender.url })) : [];
        await putState(sender.tab.id, { ...state, pageUrl: state.pageUrl || sender.tab.url || "", candidates: mergeCandidates(state.candidates, incoming) });
        return { ok: true };
      });
    }
    if (message?.type === "download" && (fromPopup || sender.tab)) return download(message, sender, { allowCookies: fromPopup });
    if (!fromPopup) throw new Error("不明なリクエストです。");
    if (message.type === "snapshot") {
      return serial(message.tabId, async () => {
        const tab = await chrome.tabs.get(message.tabId);
        const state = await getState(tab.id);
        const candidates = withoutHash(state.pageUrl) === withoutHash(tab.url) ? state.candidates : [];
        return { ok: true, ...state, candidates, tab: { id: tab.id, url: tab.url, title: tab.title } };
      });
    }
    if (message.type === "ping" || message.type === "open") return native({ action: message.type });
    throw new Error("不明な操作です。");
  })().then(respond, error => respond({ ok: false, error: error?.message || "操作に失敗しました。" }));
  return true;
});

function installMenus() {
  // onInstalled and onStartup can both fire after a browser update; reading lastError keeps a duplicate id quiet.
  const done = () => void chrome.runtime.lastError;
  chrome.contextMenus.removeAll(() => {
    // No targetUrlPatterns on the video item: most players use blob: sources, and the page is analyzed for those.
    chrome.contextMenus.create({ id: "save-video", title: "ClipDockでMP4保存", contexts: ["video"], documentUrlPatterns: WEB_PATTERNS }, done);
    chrome.contextMenus.create({ id: "save-link", title: "リンク先をClipDockでMP4保存", contexts: ["link"], targetUrlPatterns: WEB_PATTERNS, documentUrlPatterns: WEB_PATTERNS }, done);
    chrome.contextMenus.create({ id: "save-page", title: "このページの動画をClipDockで保存", contexts: ["page"], documentUrlPatterns: WEB_PATTERNS }, done);
  });
}
chrome.runtime.onInstalled.addListener(installMenus);
chrome.runtime.onStartup.addListener(installMenus);
chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (!tab?.id) return;
  const frameUrl = info.frameUrl || tab.url;
  const url = info.menuItemId === "save-link" ? info.linkUrl
    : info.menuItemId === "save-video" ? (isWebUrl(info.srcUrl) ? info.srcUrl : frameUrl)
    : tab.url;
  void download({ url }, { tab, url: frameUrl }).catch(error => {
    // There is no page UI for a context-menu save, so the reason goes on the toolbar icon.
    void chrome.action.setBadgeText({ tabId: tab.id, text: "!" }).catch(() => {});
    void chrome.action.setBadgeBackgroundColor({ tabId: tab.id, color: "#A04B44" }).catch(() => {});
    void chrome.action.setTitle({ tabId: tab.id, title: `ClipDock: ${error?.message || "保存できませんでした。"}` }).catch(() => {});
  });
});
