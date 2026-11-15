import test from "node:test";
import assert from "node:assert/strict";

const event = () => ({ listeners: [], addListener(fn) { this.listeners.push(fn); } });
const session = {};
let nativeCalls = [];
let cookieReads = 0;
const tab = { id: 7, url: "https://example.com/watch", title: "テスト動画", incognito: false };
const base = "chrome-extension://oeolbdjffnnmaanghklbkhfhgihepfhd/";
globalThis.chrome = {
  runtime: { id: base.split("/")[2], onMessage: event(), onInstalled: event(), getURL: path => base + path,
    async sendNativeMessage(host, request) { nativeCalls.push({ host, request }); return { ok: true }; } },
  storage: {
    session: { async get(key) { return { [key]: session[key] }; }, async set(value) { Object.assign(session, value); }, async remove(key) { delete session[key]; } },
    local: { async get(defaults) { return defaults; } },
  },
  tabs: { async get() { return { ...tab }; }, onRemoved: event() },
  webNavigation: { onCommitted: event(), onHistoryStateUpdated: event(), async getFrame() { return { url: tab.url, documentId: "doc1" }; } },
  webRequest: { onHeadersReceived: event() },
  action: { async setBadgeText() {}, async setBadgeBackgroundColor() {} },
  permissions: { async contains() { return true; } },
  cookies: { async getAllCookieStores() { return [{ id: "0", tabIds: [7] }]; }, async getAll() { cookieReads++; return [{ domain: "example.com", path: "/", name: "session", value: "secret", secure: true, httpOnly: true, hostOnly: true }]; } },
  contextMenus: { onClicked: event(), removeAll(fn) { fn(); }, create() {} },
};
await import("../../extension/background.js");
const popup = { id: chrome.runtime.id, url: base + "popup.html" };
const content = { id: chrome.runtime.id, url: tab.url, tab: { ...tab }, frameId: 0, documentId: "doc1" };
const send = (message, sender = popup) => new Promise(resolve => chrome.runtime.onMessage.listeners[0](message, sender, resolve));
const flush = () => new Promise(resolve => setImmediate(resolve));

test("background integration: discovery, navigation, consent and native bridge", async t => {
  await t.test("captures a playlist with its frame referer", async () => {
    chrome.webNavigation.onCommitted.listeners[0]({ tabId: 7, frameId: 0, timeStamp: 100, url: tab.url });
    await flush();
    chrome.webRequest.onHeadersReceived.listeners[0]({ tabId: 7, frameId: 0, statusCode: 200, timeStamp: 101, url: "https://cdn.example.com/master.m3u8?sig=abc", responseHeaders: [{ name: "Content-Type", value: "application/vnd.apple.mpegurl" }] });
    await flush();
    const state = await send({ type: "snapshot", tabId: 7 });
    assert.equal(state.candidates.length, 1); assert.equal(state.candidates[0].referer, tab.url);
  });
  await t.test("rejects obsolete document messages and clears candidates on SPA navigation", async () => {
    await send({ type: "discovered", candidates: [{ url: "https://a.test/old.mp4" }] }, { ...content, documentId: "obsolete" });
    assert.equal((await send({ type: "snapshot", tabId: 7 })).candidates.length, 1);
    chrome.webNavigation.onHistoryStateUpdated.listeners[0]({ tabId: 7, frameId: 0, timeStamp: 200, url: tab.url });
    await flush();
    assert.equal((await send({ type: "snapshot", tabId: 7 })).candidates.length, 0);
  });
  await t.test("ordinary save never reads browser cookies", async () => {
    const result = await send({ type: "download", tabId: 7, pageUrl: tab.url, url: tab.url, quality: "720" });
    assert.equal(result.ok, true); assert.equal(cookieReads, 0);
    assert.equal(nativeCalls.at(-1).host, "com.clipdock.downloader");
    assert.equal(nativeCalls.at(-1).request.quality, "720");
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
  await t.test("content scripts cannot invoke general native host actions", async () => {
    assert.equal((await send({ type: "open" }, content)).ok, false);
    assert.equal((await send({ type: "ping" })).ok, true);
    assert.deepEqual(nativeCalls.at(-1).request, { action: "ping" });
  });
  await t.test("tab closure deletes captured media URLs", async () => {
    chrome.tabs.onRemoved.listeners[0](7); await flush();
    assert.equal(session["tab-7"], undefined);
  });
});
