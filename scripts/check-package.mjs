import assert from "node:assert/strict";
import { readFileSync, existsSync, readdirSync } from "node:fs";
import { resolve, join } from "node:path";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";

const directory = resolve(process.argv[2] || "artifacts/ClipDock-win-x64");
const props = readFileSync(new URL("../Directory.Build.props", import.meta.url), "utf8");
const version = props.match(/<Version>(\d+\.\d+\.\d+)<\/Version>/)?.[1];
assert.ok(version, "Directory.Build.props must define a three-component release version");
for (const file of ["ClipDock.exe", "ClipDock.dll", "ClipDock.Core.dll", "ClipDock.deps.json", "ClipDock.runtimeconfig.json",
  "coreclr.dll", "hostpolicy.dll", "INSTALL.cmd", "Install.ps1", "UNINSTALL.cmd", "Uninstall.ps1",
  "README.md", "PRIVACY.md", "LICENSE", "THIRD-PARTY.md", "START-HERE.html", "docs/manual-checks.md", ".clipdock-package.json",
  "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll", "WebView2Loader.dll", "licenses/webview2/LICENSE.txt", "licenses/webview2/NOTICE.txt"])
  assert.ok(existsSync(join(directory, file)), `Missing standalone package file: ${file}`);
assert.ok(!existsSync(join(directory, "extension")), "Retired extension must not be packaged");
assert.ok(!readdirSync(directory).some(file => file.startsWith("ClipDock.NativeHost.")), "Retired native host must not be packaged");
const inventory = JSON.parse(readFileSync(join(directory, ".clipdock-package.json"), "utf8"));
for (const file of inventory.files) {
  assert.ok(typeof file.path === "string" && !file.path.split(/[\\/]/).some(part => part === "..") && !file.path.includes(":"));
  const contents = readFileSync(join(directory, file.path));
  assert.equal(createHash("sha256").update(contents).digest("hex"), file.sha256.toLowerCase(), `Invalid uninstall inventory hash: ${file.path}`);
}
assert.ok(inventory.files.some(file => file.path === "ClipDock.exe") && inventory.files.some(file => file.path === "Uninstall.ps1"));
for (const file of ["docs/webstore-listing-ja.md", "docs/webstore-review.md"])
  assert.ok(!existsSync(join(directory, file)), `Retired store document must not be packaged: ${file}`);
const exe = readFileSync(join(directory, "ClipDock.exe"));
assert.equal(exe.toString("ascii", 0, 2), "MZ");
const pe = exe.readUInt32LE(0x3c);
assert.equal(exe.toString("ascii", pe, pe + 4), "PE\0\0");
assert.equal(exe.readUInt16LE(pe + 4), 0x8664, "ClipDock.exe must be an x64 executable");
const checkVersion = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
  "$ErrorActionPreference = 'Stop'; @('ClipDock.exe', 'ClipDock.dll', 'ClipDock.Core.dll') | ForEach-Object { " +
  "$v = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $env:CLIPDOCK_PACKAGE_CHECK $_)); " +
  "'{0}.{1}.{2}' -f $v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart }"],
  { env: { ...process.env, CLIPDOCK_PACKAGE_CHECK: directory }, encoding: "utf8", timeout: 15000, windowsHide: true });
assert.equal(checkVersion.status, 0, String(checkVersion.error || checkVersion.stderr));
assert.deepEqual(checkVersion.stdout.trim().split(/\r?\n/), [version, version, version], "Application binary versions must match Directory.Build.props");
console.log(`PASS standalone x64 application and version ${version}`);
const runtime = JSON.parse(readFileSync(join(directory, "ClipDock.runtimeconfig.json"), "utf8"));
assert.ok(runtime.runtimeOptions.includedFrameworks.some(f => f.name === "Microsoft.WindowsDesktop.App"));
for (const framework of runtime.runtimeOptions.includedFrameworks) {
  const folder = join(directory, "licenses", "dotnet", framework.name, framework.version);
  assert.ok(["LICENSE.TXT", "LICENSE"].some(file => existsSync(join(folder, file))), `Missing ${framework.name} ${framework.version} license`);
  if (framework.name === "Microsoft.NETCore.App")
    assert.ok(existsSync(join(folder, "THIRD-PARTY-NOTICES.TXT")), "Missing .NET runtime third-party notices");
}
console.log("PASS self-contained runtime, licenses and installation files; no retired browser payload");
console.log("Package checks passed.");
