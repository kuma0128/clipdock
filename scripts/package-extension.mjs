import { copyFile, lstat, mkdir, readFile, realpath, rm, writeFile } from "node:fs/promises";
import { dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { createHash, createPublicKey } from "node:crypto";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const artifacts = resolve(root, "artifacts");
export const extensionFiles = Object.freeze([
  "background.js", "content.js", "distribution.js", "site-policy.js", "media.js", "popup.js", "popup.html", "popup.css",
  "icons/16.png", "icons/32.png", "icons/48.png", "icons/128.png", "icons/256.png",
]);

export async function prepareExtensionPackage(output = resolve(artifacts, "webstore/package")) {
  const identity = JSON.parse(await readFile(resolve(root, "extension/store-identity.json"), "utf8"));
  const publicKey = Buffer.from(identity.key, "base64");
  createPublicKey({ key: publicKey, format: "der", type: "spki" });
  const id = [...createHash("sha256").update(publicKey).digest("hex").slice(0, 32)]
    .map(character => String.fromCharCode(97 + parseInt(character, 16))).join("");
  if (id !== identity.id) throw new Error("Store public key does not match the issued extension ID.");
  output = resolve(output);
  const within = relative(artifacts, output);
  if (!within || within === ".." || within.startsWith(".." + sep) || isAbsolute(within))
    throw new Error("Extension package output must be a subfolder of artifacts.");
  await mkdir(dirname(output), { recursive: true });
  const parent = await realpath(dirname(output));
  if (await realpath(artifacts) !== artifacts || parent !== artifacts && !parent.startsWith(artifacts + sep))
    throw new Error("Extension package output cannot pass through a symbolic link.");
  const previous = await lstat(output).catch(error => { if (error.code !== "ENOENT") throw error; });
  if (previous?.isSymbolicLink()) throw new Error("Extension package output cannot be a symbolic link.");
  await rm(output, { recursive: true, force: true });
  await mkdir(resolve(output, "icons"), { recursive: true });
  for (const file of extensionFiles) await copyFile(resolve(root, "extension", file), resolve(output, file));
  await writeFile(resolve(output, "distribution.js"), 'globalThis.CLIPDOCK_DISTRIBUTION = "store";\n');
  const manifest = JSON.parse(await readFile(resolve(root, "extension/manifest.json"), "utf8"));
  manifest.key = identity.key;
  manifest.permissions = manifest.permissions.filter(permission => permission !== "activeTab");
  manifest.homepage_url = "https://github.com/kuma0128/clipdock";
  manifest.description = "Chromeの動画をWindows用ClipDockアプリでMP4として保存します。別途Windowsアプリが必要です。YouTubeには対応していません。";
  await import("../extension/distribution.js");
  await import("../extension/site-policy.js");
  const excluded = globalThis.ClipDockSitePolicy.excludedHosts.flatMap(host => ["http", "https"].map(scheme => `${scheme}://*.${host}/*`));
  for (const script of manifest.content_scripts) script.exclude_matches = excluded;
  await writeFile(resolve(output, "manifest.json"), JSON.stringify(manifest, null, 2) + "\n");
  return output;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(await prepareExtensionPackage(process.argv[2]));
}
