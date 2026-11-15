import assert from "node:assert/strict";
import { readFileSync, existsSync, readdirSync } from "node:fs";
import { resolve, join } from "node:path";
import { spawnSync } from "node:child_process";

const directory = resolve(process.argv[2] || "artifacts/ClipDock-win-x64");
const host = join(directory, "ClipDock.NativeHost.exe");
const manifest = JSON.parse(readFileSync(join(directory, "extension/manifest.json"), "utf8"));
const origin = "chrome-extension://oeolbdjffnnmaanghklbkhfhgihepfhd/";
const storeOrigin = "chrome-extension://okehkphoehepefjddoiinohnhpjghefe/";
function invoke(input, caller = origin) {
  const result = spawnSync(host, [caller], { input, timeout: 15000, windowsHide: true, maxBuffer: 1024 * 1024 });
  if (result.error) throw result.error;
  return result;
}
function frame(value) {
  const body = Buffer.from(JSON.stringify(value));
  const size = Buffer.alloc(4); size.writeUInt32LE(body.length);
  return Buffer.concat([size, body]);
}
function decode(result) {
  assert.ok(result.stdout.length >= 4, result.stderr.toString());
  const size = result.stdout.readUInt32LE(0);
  assert.equal(size, result.stdout.length - 4, "stdout must contain exactly one native message");
  return JSON.parse(result.stdout.subarray(4).toString("utf8"));
}
const health = invoke(frame({ action: "ping" }));
assert.equal(health.status, 0);
const response = decode(health);
assert.equal(response.ok, true);
assert.equal(response.version, manifest.version, "Native host and extension versions must match");
console.log(`PASS packaged native protocol handshake (engines ${response.ready ? "ready" : "not bundled"})`);
const storeHealth = invoke(frame({ action: "ping" }), storeOrigin);
assert.equal(storeHealth.status, 0);
assert.equal(decode(storeHealth).ok, true);
assert.equal(decode(storeHealth).version, manifest.version);
console.log("PASS packaged native protocol accepts the public store origin");
const inbox = join(process.env.LOCALAPPDATA, "ClipDock", "inbox");
const queuedFiles = () => existsSync(inbox) ? readdirSync(inbox).filter(name => /\.request(?:\.tmp)?$/.test(name)) : [];
const beforeStoreRequests = new Set(queuedFiles());
for (const restriction of [{}, { restrictYouTube: false }]) {
  for (const fields of [{ url: "https://www.youtube.com/watch?v=store-policy-test" },
    { url: "https://example.com/video.mp4", pageUrl: "https://www.youtube.com/watch?v=store-policy-test" },
    { url: "https://example.com/video.mp4", referer: "https://rr1.googlevideo.com/video.mp4" }]) {
    const result = invoke(frame({ action: "download", ...fields, ...restriction }), storeOrigin);
    assert.equal(result.status, 1);
    const rejected = decode(result);
    assert.equal(rejected.ok, false);
    assert.match(rejected.error, /Chrome ウェブストア版はYouTube/);
  }
}
assert.deepEqual(queuedFiles().filter(name => !beforeStoreRequests.has(name)), [], "Rejected store requests must not enter the inbox");
console.log("PASS store origin enforces YouTube restrictions before creating queued files");
assert.equal(decode(invoke(frame({ action: "execute", command: "not-run" }))).ok, false);
console.log("PASS unsupported native action rejected");
const invalidSize = Buffer.alloc(4); invalidSize.writeInt32LE(2147483647);
assert.equal(decode(invoke(invalidSize)).ok, false);
console.log("PASS oversized native message rejected");
const unauthorized = invoke(frame({ action: "ping" }), "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/");
assert.equal(unauthorized.status, 1); assert.equal(unauthorized.stdout.length, 0);
console.log("PASS unauthorized extension origin rejected");
const runtime = JSON.parse(readFileSync(join(directory, "ClipDock.runtimeconfig.json"), "utf8"));
assert.ok(runtime.runtimeOptions.includedFrameworks.some(f => f.name === "Microsoft.WindowsDesktop.App"));
for (const framework of runtime.runtimeOptions.includedFrameworks) {
  const folder = join(directory, "licenses", "dotnet", framework.name, framework.version);
  assert.ok(["LICENSE.TXT", "LICENSE"].some(file => existsSync(join(folder, file))), `Missing ${framework.name} ${framework.version} license`);
  if (framework.name === "Microsoft.NETCore.App")
    assert.ok(existsSync(join(folder, "THIRD-PARTY-NOTICES.TXT")), "Missing .NET runtime third-party notices");
}
console.log("PASS licenses for the bundled .NET runtime");
for (const file of ["ClipDock.exe", "INSTALL.cmd", "Install.ps1", "UNINSTALL.cmd", "Uninstall.ps1", "README.md", "PRIVACY.md", "LICENSE", "THIRD-PARTY.md", "START-HERE.html", "docs/manual-checks.md", ...Object.values(manifest.icons).map(f => "extension/" + f)])
  assert.ok(existsSync(join(directory, file)), file);
console.log("PASS self-contained runtime and installation files present");
const bundled = existsSync(join(directory, "tools"));
if (bundled) {
  for (const file of ["versions.json", "licenses"])
    assert.ok(existsSync(join(directory, "tools", file)), `Missing bundled engine metadata: ${file}`);
}
for (const [exe, arg] of [["yt-dlp.exe", "--version"], ["ffmpeg.exe", "-version"], ["ffprobe.exe", "-version"], ["deno.exe", "--version"]]) {
  if (!bundled) break;
  const path = join(directory, "tools", exe);
  assert.ok(existsSync(path), `Incomplete engine bundle: ${exe}`);
  const result = spawnSync(path, [arg], { timeout: 20000, windowsHide: true, maxBuffer: 1024 * 1024 });
  assert.equal(result.status, 0, `${exe}: ${result.error || result.stderr}`);
  console.log(`PASS ${exe}: ${result.stdout.toString().split(/\r?\n/)[0]}`);
}
console.log("Package checks passed.");
