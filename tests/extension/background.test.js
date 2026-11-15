import test from "node:test";
import assert from "node:assert/strict";

const event = () => ({ listeners: [], addListener(fn) { this.listeners.push(fn); } });
const session = {};
let nativeCalls = [];
let nativeReply = async () => ({ ok: true });
let cookieReads = 0;
const menus = [];
const actionCalls = [];
const tab = { id: 7, url: "https://example.com/watch", title: "テスト動画", incognito: false };
const base = "chrome-extension://oeolbdjffnnmaanghklbkhfhgihepfhd/";
globalThis.chrome = {
  runtime: { id: base.split("/")[2], onMessage: event(), onInstalled: event(), onStartup: event(), getURL: path => base + path,
    async sendNativeMessage(host, request) { nativeCalls.push({ host, request }); return nativeReply(request); } },
  storage: {
    session: { async get(key) { return { [key]: session[key] }; }, async set(value) { Object.assign(session, value); }, async remove(key) { delete session[key]; } },
    local: { async get(defaults) { return defaults; } },
  },
  tabs: { async get() { return { ...tab }; }, onRemoved: event(), onReplaced: event() },
  webNavigation: { onCommitted: event(), onHistoryStateUpdated: event(), async getFrame() { return { url: tab.url, documentId: "doc1" }; } },
  webRequest: { onHeadersReceived: event() },
  action: {
    async setBadgeText(details) { actionCalls.push(["badge", details]); }, async setBadgeBackgroundColor() {},
    async setTitle(details) { actionCalls.push(["title", details]); },
  },
  permissions: { async contains() { return true; } },
  cookies: { async getAllCookieStores() { return [{ id: "0", tabIds: [7] }]; }, async getAll() { cookieReads++; return [{ domain: "example.com", path: "/", name: "session", value: "secret", secure: true, httpOnly: true, hostOnly: true }]; } },
  contextMenus: { onClicked: event(), removeAll(fn) { menus.length = 0; fn(); }, create(item, done) { menus.push(item); done?.(); } },
};
await import("../../extension/background.js");
const popup = { id: chrome.runtime.id, url: base + "popup.html" };
const content = { id: chrome.runtime.id, url: tab.url, tab: { ...tab }, frameId: 0, documentId: "doc1" };
const send = (message, sender = popup) => new Promise(resolve => chrome.runtime.onMessage.listeners[0](message, sender, resolve));
const flush = () => new Promise(resolve => setImmediate(resolve));
const candidates = async () => (await send({ type: "snapshot", tabId: 7 })).candidates;

test("background integration: discovery, navigation, consent and native bridge", async t => {
  await t.test("captures a playlist with its frame referer", async () => {
    chrome.webNavigation.onCommitted.listeners[0]({ tabId: 7, frameId: 0, timeStamp: 100, url: tab.url });
    await flush();
    chrome.webRequest.onHeadersReceived.listeners[0]({ tabId: 7, frameId: 0, statusCode: 200, timeStamp: 101, url: "https://cdn.example.com/master.m3u8?sig=abc", responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }] });
    await flush();
    const state = await send({ type: "snapshot", tabId: 7 });
    assert.equal(state.candidates.length, 1); assert.equal(state.candidates[0].referer, tab.url);
  });
  await t.test("rejects obsolete document messages", async () => {
    await send({ type: "discovered", candidates: [{ url: "https://a.test/old.mp4" }] }, { ...content, documentId: "obsolete" });
    assert.equal((await candidates()).length, 1);
  });
  await t.test("keeps candidates when the page only updates its history state", async () => {
    // replaceState with the same URL (scroll restoration) and hash-only changes are not navigations.
    chrome.webNavigation.onHistoryStateUpdated.listeners[0]({ tabId: 7, frameId: 0, timeStamp: 150, url: tab.url });
    chrome.webNavigation.onHistoryStateUpdated.listeners[0]({ tabId: 7, frameId: 0, timeStamp: 160, url: tab.url + "#t=30" });
    await flush();
    assert.equal((await candidates()).length, 1);
    // Requests made before the skipped update are still accepted afterwards.
    chrome.webRequest.onHeadersReceived.listeners[0]({ tabId: 7, frameId: 0, statusCode: 200, timeStamp: 170, url: "https://cdn.example.com/clip.mp4", responseHeaders: [{ name: "Content-Type", value: "video/mp4" }] });
    await flush();
    assert.equal((await candidates()).length, 2);
  });
  await t.test("clears candidates on SPA navigation to another URL", async () => {
    chrome.webNavigation.onHistoryStateUpdated.listeners[0]({ tabId: 7, frameId: 0, timeStamp: 200, url: "https://example.com/watch?v=next" });
    await flush();
    assert.equal((await candidates()).length, 0);
    // A request that started before the navigation belongs to the previous video.
    chrome.webRequest.onHeadersReceived.listeners[0]({ tabId: 7, frameId: 0, statusCode: 200, timeStamp: 190, url: "https://cdn.example.com/previous.mp4", responseHeaders: [{ name: "Content-Type", value: "video/mp4" }] });
    await flush();
    assert.equal((await candidates()).length, 0);
  });
  await t.test("ordinary save never reads browser cookies", async () => {
    const result = await send({ type: "download", tabId: 7, pageUrl: tab.url, url: tab.url, quality: "720" });
    assert.equal(result.ok, true); assert.equal(cookieReads, 0);
    assert.equal(nativeCalls.at(-1).host, "com.clipdock.downloader");
    assert.equal(nativeCalls.at(-1).request.quality, "720");
    assert.deepEqual(nativeCalls.at(-1).request.cookies, []);
  });
  await t.test("content scripts cannot opt in to cookie transfer", async () => {
    const result = await send({ type: "download", url: tab.url, useCookies: true }, content);
    assert.equal(result.ok, true); assert.equal(cookieReads, 0);
    assert.deepEqual(nativeCalls.at(-1).request.cookies, []);
  });
  await t.test("cookie transfer needs an explicit per-save selection", async () => {
    await send({ type: "download", tabId: 7, url: tab.url, useCookies: true });
    assert.ok(cookieReads > 0); assert.equal(nativeCalls.at(-1).request.cookies.length, 1);
  });
  await t.test("rejects stale popup URLs and invalid quality without launching the host", async () => {
    const before = nativeCalls.length;
    assert.equal((await send({ type: "download", tabId: 7, pageUrl: "https://old.test/" })).ok, false);
    assert.equal((await send({ type: "download", tabId: 7, quality: "--exec calc" })).ok, false);
    assert.equal(nativeCalls.length, before);
  });
  await t.test("rejects requests the native host would refuse for size", async () => {
    const before = nativeCalls.length;
    const result = await send({ type: "download", tabId: 7, url: "https://example.com/v", referer: "https://example.com/?" + "a".repeat(16000) });
    assert.equal(result.ok, true); assert.equal(nativeCalls.length, before + 1);
    const realTitle = tab.title;
    tab.title = "あ".repeat(200 * 1024);
    try {
      const oversized = await send({ type: "download", tabId: 7, url: "https://example.com/v" });
      assert.equal(oversized.ok, false); assert.match(oversized.error, /長すぎ/);
      assert.equal(nativeCalls.length, before + 1);
    } finally { tab.title = realTitle; }
  });
  await t.test("tells setup problems apart from errors reported by the app", async () => {
    nativeReply = async () => { throw new Error("Specified native messaging host not found."); };
    assert.match((await send({ type: "ping" })).error, /Chrome連携を設定/);
    nativeReply = async () => ({ ok: false, error: "待機中のリクエストが多すぎます。connect host" });
    assert.equal((await send({ type: "ping" })).error, "待機中のリクエストが多すぎます。connect host");
    nativeReply = async () => ({ ok: true });
  });
  await t.test("content scripts cannot invoke general native host actions", async () => {
    assert.equal((await send({ type: "open" }, content)).ok, false);
    assert.equal((await send({ type: "ping" })).ok, true);
    assert.deepEqual(nativeCalls.at(-1).request, { action: "ping" });
  });
  await t.test("context menu covers blob-backed videos and reports failures on the toolbar icon", async () => {
    chrome.runtime.onInstalled.listeners[0]();
    const video = menus.find(m => m.id === "save-video");
    assert.deepEqual(menus.map(m => m.id).sort(), ["save-link", "save-page", "save-video"]);
    assert.equal(video.targetUrlPatterns, undefined, "blob: sources must not hide the video item");
    const click = (info, target = { ...tab }) => chrome.contextMenus.onClicked.listeners[0](info, target);
    click({ menuItemId: "save-video", srcUrl: "blob:https://player.example.com/1", frameUrl: "https://player.example.com/embed/1" });
    await flush();
    assert.equal(nativeCalls.at(-1).request.url, "https://player.example.com/embed/1");
    assert.equal(nativeCalls.at(-1).request.referer, "https://player.example.com/embed/1");
    assert.deepEqual(nativeCalls.at(-1).request.cookies, []);
    click({ menuItemId: "save-link", linkUrl: "https://example.com/clip.mp4" });
    await flush();
    assert.equal(nativeCalls.at(-1).request.url, "https://example.com/clip.mp4");
    const before = nativeCalls.length;
    actionCalls.length = 0;
    click({ menuItemId: "save-page" }, { ...tab, url: "chrome://settings/" });
    await flush();
    assert.equal(nativeCalls.length, before);
    assert.ok(actionCalls.some(([kind, details]) => kind === "badge" && details.text === "!"));
    assert.ok(actionCalls.some(([kind, details]) => kind === "title" && details.title.startsWith("ClipDock: ")));
  });
  await t.test("tab closure and replacement delete captured media URLs", async () => {
    chrome.tabs.onRemoved.listeners[0](7); await flush();
    assert.equal(session["tab-7"], undefined);
    session["tab-9"] = { candidates: [], since: 0, pageUrl: "" };
    chrome.tabs.onReplaced.listeners[0](10, 9); await flush();
    assert.equal(session["tab-9"], undefined);
  });
});
