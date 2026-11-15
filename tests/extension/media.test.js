import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, existsSync } from "node:fs";
import { isWebUrl, classifyMedia, mergeCandidates } from "../../extension/media.js";

test("accepts only credential-free HTTP(S) URLs", () => {
  for (const url of ["file:///secret", "blob:https://a.test/1", "javascript:alert(1)", "https://user:pass@a.test/v", "--exec", "https://a.test/\nX", null]) assert.equal(isWebUrl(url), false);
  assert.equal(isWebUrl("https://a.test/v?signature=a%26b"), true);
});
test("recognizes HLS/DASH even without file extension", () => {
  assert.equal(classifyMedia("https://a.test/index.M3U8?token=x"), "HLS");
  assert.equal(classifyMedia("https://a.test/play", "application/vnd.apple.mpegurl; charset=utf-8"), "HLS");
  assert.equal(classifyMedia("https://a.test/play", "application/dash+xml"), "DASH");
  assert.equal(classifyMedia("https://a.test/play", "video/mp4"), "MP4");
});
test("does not mistake fragments, audio or blob references for standalone videos", () => {
  for (const url of ["https://a.test/chunk.ts", "https://a.test/1.m4s", "https://a.test/a.m4a", "https://a.test/v?range=0-100", "blob:https://a.test/1"])
    assert.equal(classifyMedia(url, "video/mp4"), null);
  assert.equal(classifyMedia("https://a.test/chunk", "video/mp2t"), null);
});
test("deduplicates exact signed URLs without destroying signatures", () => {
  const url = "https://a.test/v.mp4?signature=very%2Blong&expires=2";
  const merged = mergeCandidates([{ url, size: 123 }], [{ url, title: "updated", kind: "MP4" }, { url: "javascript:alert(1)" }]);
  assert.equal(merged.length, 1); assert.equal(merged[0].url, url); assert.equal(merged[0].size, 123);
});
test("bounds storage size and sanitizes metadata", () => {
  const data = Array.from({ length: 70 }, (_, i) => ({ url: `https://a.test/${i}.mp4`, title: "x".repeat(300), referer: "file:///secret" }));
  const result = mergeCandidates([], data);
  assert.equal(result.length, 50); assert.equal(result[0].title.length, 180); assert.equal(result[0].referer, "");
});
test("public manifest key, host registration and packaged files agree", () => {
  const manifest = JSON.parse(readFileSync(new URL("../../extension/manifest.json", import.meta.url)));
  const id = [...createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest().subarray(0, 16)].flatMap(b => [b >> 4, b & 15]).map(n => String.fromCharCode(97 + n)).join("");
  const integration = readFileSync(new URL("../../src/ClipDock.Core/Integration.cs", import.meta.url), "utf8");
  const installer = readFileSync(new URL("../../scripts/Install.ps1", import.meta.url), "utf8");
  assert.ok(integration.includes(id)); assert.ok(installer.includes(id));
  assert.equal(manifest.manifest_version, 3);
  assert.ok(!manifest.permissions.includes("cookies")); assert.ok(manifest.optional_permissions.includes("cookies"));
  for (const file of [...Object.values(manifest.icons), manifest.action.default_popup, manifest.background.service_worker, ...manifest.content_scripts[0].js])
    assert.ok(existsSync(new URL(`../../extension/${file}`, import.meta.url)), file);
});
