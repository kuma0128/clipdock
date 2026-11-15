import test from "node:test";
import assert from "node:assert/strict";

class Element {
  checked = false;
  value = "1080";
  children = [];
  listeners = {};
  classList = { toggle() {} };
  addEventListener(type, callback) { this.listeners[type] = callback; }
  setAttribute() {}
  append(...children) { this.children.push(...children); }
  replaceChildren() { this.children = []; }
}
const elements = new Map();
const element = id => {
  if (!elements.has(id)) elements.set(id, new Element());
  return elements.get(id);
};
globalThis.document = {
  getElementById: element, createElement: () => new Element(),
  querySelectorAll: () => element("candidates").children.flatMap(row => row.children.slice(-1)),
};
let tab = { id: 7, url: "https://example.com/watch", title: "Video" };
const downloads = [];
let failSave = false;
globalThis.chrome = {
  runtime: { async sendMessage(message) {
    if (message.type === "snapshot") return { ok: true, tab: { ...tab }, candidates: [] };
    if (message.type === "download") {
      downloads.push(message);
      return failSave ? { ok: false, error: "offline" } : { ok: true };
    }
    return { ok: true, ready: true };
  } },
  tabs: { async query() { return [{ ...tab }]; }, async sendMessage() {} },
  storage: { local: { async get(defaults) { return defaults; }, async set() {} }, onChanged: { addListener() {} } },
  permissions: { async request() { return true; } },
};
await import("../../extension/popup.js");
await new Promise(resolve => setImmediate(resolve));

test("cookie consent is consumed by a single save, including a failed save", async () => {
  for (const fails of [false, true]) {
    failSave = fails;
    element("cookies").checked = true;
    await element("save-page").listeners.click();
    assert.equal(downloads.at(-1).useCookies, true);
    assert.equal(element("cookies").checked, false);
    await element("save-page").listeners.click();
    assert.equal(downloads.at(-1).useCookies, false);
  }
});

test("navigation clears cookie consent before the next save", async () => {
  element("cookies").checked = true;
  tab = { ...tab, url: "https://another.example/watch" };
  await element("refresh").listeners.click();
  assert.equal(element("cookies").checked, false);
});
