import test, { after } from "node:test";
import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { isRestrictedUrl as localRestriction } from "../../extension/media.js";
import { extensionFiles, prepareExtensionPackage } from "../../scripts/package-extension.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const artifacts = resolve(root, "artifacts");
await mkdir(artifacts, { recursive: true });
const temporary = await mkdtemp(resolve(artifacts, "extension-tests-"));
after(() => rm(temporary, { recursive: true, force: true }));
const output = resolve(temporary, "package");
await prepareExtensionPackage(output);
const storeMedia = await import(pathToFileURL(resolve(output, "media.js")));

test("store rules reject YouTube hosts, subdomains, trailing dots and blob origins without affecting local builds", () => {
  for (const url of ["https://youtube.com/watch?v=1", "https://www.youtube.com./watch?v=1", "https://youtu.be/a",
    "https://www.youtube-nocookie.com/embed/a", "https://r1.googlevideo.com/videoplayback", "https://youtubei.googleapis.com/youtubei/v1/player",
    "blob:https://www.youtube.com/123"]) {
    assert.equal(localRestriction(url), false, url);
    assert.equal(storeMedia.isRestrictedUrl(url), true, url);
  }
  for (const url of ["https://notyoutube.com/video", "https://youtube.com.example/video", "https://example.com/youtube.com", "https://x.com/example/status/1"])
    assert.equal(storeMedia.isRestrictedUrl(url), false, url);
  assert.equal(storeMedia.classifyMedia("https://r1.googlevideo.com/clip.mp4"), null);
  assert.deepEqual(storeMedia.mergeCandidates([{ url: "https://r1.googlevideo.com/clip.mp4" }], [
    { url: "https://cdn.example.com/clip.mp4", referer: "https://youtube.com/watch?v=1" },
  ]), []);
});

test("store packaging removes the local identity and unnecessary permission and ships only extension files", async () => {
  const manifest = JSON.parse(await readFile(resolve(output, "manifest.json"), "utf8"));
  assert.equal(manifest.key, undefined);
  assert.equal(manifest.permissions.includes("activeTab"), false);
  assert.deepEqual(manifest.optional_permissions, ["cookies"]);
  assert.deepEqual(manifest.content_scripts[0].js, ["distribution.js", "site-policy.js", "content.js"]);
  assert.ok(manifest.content_scripts[0].exclude_matches.includes("https://*.youtube.com/*"));
  assert.ok(manifest.content_scripts[0].exclude_matches.includes("https://*.googlevideo.com/*"));
  assert.ok(manifest.description.includes("Windowsアプリが必要"));
  const files = (await readdir(output, { recursive: true })).filter(file => file !== "icons").map(file => file.replaceAll("\\", "/")).sort();
  assert.deepEqual(files, [...extensionFiles, "manifest.json"].sort());
  await writeFile(resolve(output, "obsolete-secret.txt"), "not for distribution");
  await prepareExtensionPackage(output);
  assert.equal((await readdir(output)).includes("obsolete-secret.txt"), false);
  await assert.rejects(prepareExtensionPackage(artifacts), /subfolder/);
  await assert.rejects(prepareExtensionPackage(resolve(root, "extension")), /subfolder/);
  const source = JSON.parse(await readFile(resolve(root, "extension/manifest.json"), "utf8"));
  assert.ok(source.key);
  assert.ok((await readFile(resolve(root, "extension/distribution.js"), "utf8")).includes('"local"'));
});

const event = () => ({ listeners: [], addListener(fn) { this.listeners.push(fn); } });
const session = {};
let tab = { id: 7, url: "https://example.com/watch", title: "Video", incognito: false };
let frameUrl = tab.url;
const nativeCalls = [];
let cookieReads = 0;
const origin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/";
globalThis.chrome = {
  runtime: { id: origin.split("/")[2], onMessage: event(), onInstalled: event(), onStartup: event(), getURL: path => origin + path,
    async sendNativeMessage(_host, request) { nativeCalls.push(request); return { ok: true }; } },
  storage: {
    session: { async get(key) { return { [key]: session[key] }; }, async set(value) { Object.assign(session, value); }, async remove(key) { delete session[key]; } },
    local: { async get(defaults) { return defaults; } },
  },
  tabs: { async get() { return { ...tab }; }, onRemoved: event(), onReplaced: event() },
  webNavigation: { onCommitted: event(), onHistoryStateUpdated: event(), async getFrame() { return { url: frameUrl, documentId: "doc1" }; } },
  webRequest: { onBeforeRequest: event(), onHeadersReceived: event(), onCompleted: event(), onErrorOccurred: event() },
  action: { async setBadgeText() {}, async setBadgeBackgroundColor() {}, async setTitle() {} },
  permissions: { async contains() { return true; } },
  cookies: { async getAllCookieStores() { return [{ id: "0", tabIds: [7] }]; }, async getAll() { cookieReads++; return []; } },
  contextMenus: { onClicked: event(), removeAll(callback) { callback(); }, create(_item, callback) { callback?.(); } },
};
await import(pathToFileURL(resolve(output, "background.js")));
const popup = { id: chrome.runtime.id, url: origin + "popup.html" };
const send = (message, sender = popup) => new Promise(done => chrome.runtime.onMessage.listeners[0](message, sender, done));
const flush = () => new Promise(resolve => setImmediate(resolve));

test("store downloads reject restricted pages, targets, referers, content frames and context menus before reading cookies", async () => {
  const blocked = "https://www.youtube.com/watch?v=1";
  for (const extra of [{ url: blocked }, { referer: blocked }, { url: "https://r2.googlevideo.com/v.mp4" }, { url: "blob:https://youtube.com/id" }]) {
    const response = await send({ type: "download", tabId: 7, url: tab.url, useCookies: true, ...extra });
    assert.equal(response.ok, false);
    assert.match(response.error, /YouTube/);
  }
  const content = { id: chrome.runtime.id, url: blocked, tab: { ...tab }, frameId: 2, documentId: "doc1" };
  assert.equal((await send({ type: "download", url: "https://cdn.example.com/video.mp4" }, content)).ok, false);
  tab.url = blocked;
  assert.equal((await send({ type: "download", tabId: 7, url: "https://cdn.example.com/video.mp4", useCookies: true })).ok, false);
  chrome.contextMenus.onClicked.listeners[0]({ menuItemId: "save-video", srcUrl: "blob:https://youtube.com/id" }, { ...tab });
  await flush();
  tab.url = "https://example.com/watch";
  chrome.contextMenus.onClicked.listeners[0]({ menuItemId: "save-link", linkUrl: blocked }, { ...tab });
  await flush();
  assert.equal(nativeCalls.length, 0);
  assert.equal(cookieReads, 0);
  assert.equal((await send({ type: "download", tabId: 7, url: tab.url, restrictYouTube: false })).ok, true);
  assert.equal(nativeCalls.length, 1);
  assert.equal(nativeCalls[0].restrictYouTube, true);
});

test("store discovery removes restricted URLs, pages, and embedded frames from every candidate source", async () => {
  const content = { id: chrome.runtime.id, url: tab.url, tab: { ...tab }, frameId: 0, documentId: "doc1" };
  await send({ type: "discovered", candidates: [{ url: "https://r1.googlevideo.com/v.mp4" }] }, content);
  await send({ type: "discovered", candidates: [{ url: "https://cdn.example.com/v.mp4" }] }, { ...content, url: "https://www.youtube.com/embed/1" });
  const headers = chrome.webRequest.onHeadersReceived.listeners[0];
  const response = { tabId: 7, frameId: 0, documentId: "doc1", timeStamp: 100, statusCode: 200, url: "https://cdn.example.com/v.mp4", responseHeaders: [{ name: "Content-Type", value: "video/mp4" }] };
  frameUrl = "https://www.youtube.com/embed/1";
  headers(response);
  await flush();
  frameUrl = tab.url;
  headers({ ...response, initiator: "https://www.youtube.com" });
  await flush();
  tab.url = "https://www.youtube.com/watch?v=1";
  headers(response);
  await flush();
  assert.deepEqual((await send({ type: "snapshot", tabId: 7 })).candidates, []);
  tab.url = "https://example.com/watch";
  assert.deepEqual((await send({ type: "snapshot", tabId: 7 })).candidates, []);
  headers(response);
  await flush();
  assert.equal((await send({ type: "snapshot", tabId: 7 })).candidates.length, 1);
});
