import { HOST, QUALITIES, isWebUrl, classifyMedia, mergeCandidates, mediaTitle } from "./media.js";

const locks = new Map();
const keyFor = tabId => `tab-${tabId}`;
function serial(tabId, action) {
  const previous = locks.get(tabId) || Promise.resolve();
  const next = previous.catch(() => {}).then(action);
  locks.set(tabId, next);
  next.finally(() => { if (locks.get(tabId) === next) locks.delete(tabId); }).catch(() => {});
  return next;
}
async function getState(tabId) {
  const key = keyFor(tabId);
  return (await chrome.storage.session.get(key))[key] || { candidates: [], since: 0, pageUrl: "" };
}
async function putState(tabId, state) {
  await chrome.storage.session.set({ [keyFor(tabId)]: state });
  await chrome.action.setBadgeText({ tabId, text: state.candidates.length ? String(state.candidates.length) : "" }).catch(() => {});
  await chrome.action.setBadgeBackgroundColor({ tabId, color: "#427758" }).catch(() => {});
}
function reset(details) {
  if (details.frameId !== 0) return;
  void serial(details.tabId, () => putState(details.tabId, { candidates: [], since: details.timeStamp, pageUrl: details.url })).catch(() => {});
}
chrome.webNavigation.onCommitted.addListener(reset);
chrome.webNavigation.onHistoryStateUpdated.addListener(reset);
chrome.tabs.onRemoved.addListener(tabId => { void serial(tabId, () => chrome.storage.session.remove(keyFor(tabId))); });

chrome.webRequest.onHeadersReceived.addListener(details => {
  if (details.tabId < 0 || details.statusCode < 200 || details.statusCode >= 300) return;
  const header = name => details.responseHeaders?.find(h => h.name.toLowerCase() === name)?.value || "";
  const kind = classifyMedia(details.url, header("content-type"));
  if (!kind) return;
  void serial(details.tabId, async () => {
    const tab = await chrome.tabs.get(details.tabId);
    if (tab.incognito) return;
    const state = await getState(details.tabId);
    if (details.timeStamp < state.since) return;
    const frame = await chrome.webNavigation.getFrame({ tabId: details.tabId, frameId: details.frameId }).catch(() => null);
    const candidate = { url: details.url, kind, title: mediaTitle(details.url),
      referer: isWebUrl(frame?.url) ? frame.url : tab.url,
      size: details.statusCode === 200 ? Number(header("content-length")) : 0 };
    await putState(details.tabId, { ...state, candidates: mergeCandidates(state.candidates, [candidate]) });
  }).catch(() => {});
}, { urls: ["http://*/*", "https://*/*"], types: ["media", "xmlhttprequest", "other", "main_frame", "sub_frame"] }, ["responseHeaders"]);

async function native(request) {
  try {
    const result = await chrome.runtime.sendNativeMessage(HOST, request);
    if (!result?.ok) throw new Error(result?.error || "ClipDockからの応答がありません。");
    return result;
  } catch (error) {
    const message = String(error?.message || error);
    if (/host|native|connect|forbidden/i.test(message)) throw new Error("ClipDockを起動して「Chrome連携を設定」を押してください。アプリの場所を変えた場合も再設定が必要です。");
    throw error;
  }
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

async function download(message, sender) {
  const tab = sender.tab || await chrome.tabs.get(message.tabId);
  if (tab.incognito || !isWebUrl(tab.url)) throw new Error("通常のhttp/httpsページで動画を開いてください。");
  if (message.pageUrl && message.pageUrl !== tab.url) throw new Error("ページが移動しました。拡張を開き直してください。");
  const url = isWebUrl(message.url) ? message.url : tab.url;
  const referer = sender.tab && isWebUrl(sender.url) ? sender.url : isWebUrl(message.referer) ? message.referer : tab.url;
  const settings = await chrome.storage.local.get({ quality: "1080" });
  const quality = message.quality || settings.quality;
  if (!QUALITIES.has(quality)) throw new Error("画質を選び直してください。");
  const cookies = message.useCookies === true ? await cookiesFor(tab, [tab.url, url, referer]) : [];
  return native({ action: "download", url, pageUrl: tab.url, referer, title: tab.title || "動画", quality, userAgent: navigator.userAgent, cookies });
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
        await putState(sender.tab.id, { ...state, candidates: mergeCandidates(state.candidates, incoming) });
        return { ok: true };
      });
    }
    if (message?.type === "download" && (fromPopup || sender.tab)) return download(message, sender);
    if (!fromPopup) throw new Error("不明なリクエストです。");
    if (message.type === "snapshot") {
      const tab = await chrome.tabs.get(message.tabId);
      return { ok: true, tab: { id: tab.id, url: tab.url, title: tab.title }, ...(await getState(tab.id)) };
    }
    if (message.type === "ping" || message.type === "open") return native({ action: message.type });
    throw new Error("不明な操作です。");
  })().then(respond, error => respond({ ok: false, error: error?.message || "操作に失敗しました。" }));
  return true;
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: "save-video", title: "ClipDockでMP4保存", contexts: ["video", "link"], targetUrlPatterns: ["http://*/*", "https://*/*"] });
    chrome.contextMenus.create({ id: "save-page", title: "このページの動画をClipDockで保存", contexts: ["page"] });
  });
});
chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (!tab?.id) return;
  const url = info.menuItemId === "save-page" ? tab.url : info.srcUrl || info.linkUrl || tab.url;
  void download({ url }, { tab, url: info.frameUrl || tab.url }).catch(() => {
    void chrome.action.setBadgeText({ tabId: tab.id, text: "!" });
    void chrome.action.setBadgeBackgroundColor({ tabId: tab.id, color: "#A04B44" });
  });
});
