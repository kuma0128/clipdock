// Static checks for the website: a release bump, a new page or a translation edit touches many HTML files,
// and these catch what a reviewer would otherwise have to click through by hand. Only Node built-ins are used.
import assert from "node:assert/strict";
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { onRequest } from "../functions/downloads/[[file]].js";
import { render, currentLastmod, visibleQuestions } from "../scripts/site-metadata.mjs";

const root = fileURLToPath(new URL("../website/", import.meta.url));
const origin = "https://clipdock-d6z.pages.dev";
const version = readFileSync(new URL("../Directory.Build.props", import.meta.url), "utf8").match(/<Version>(\d+\.\d+\.\d+)<\/Version>/)[1];
const languages = { "": "ja", "en/": "en", "zh-cn/": "zh-Hans", "es/": "es" };
const releaseFiles = [`ClipDock-${version}-win-x64-unsigned-setup.exe`, `ClipDock-${version}-mac-arm64-unsigned.dmg`, `ClipDock-${version}-mac-x64-unsigned.dmg`];

function htmlFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry =>
    entry.isDirectory() ? htmlFiles(join(directory, entry.name)) : entry.name.endsWith(".html") ? [join(directory, entry.name)] : []);
}

// Pages are served with pretty URLs: help.html is /help and en/index.html is /en/.
const pages = htmlFiles(root).map(file => {
  const path = relative(root, file).split(sep).join("/");
  const html = readFileSync(file, "utf8");
  const url = "/" + path.replace(/(^|\/)index\.html$/, "$1").replace(/\.html$/, "");
  return { path, url, html, prefix: Object.keys(languages).find(p => p && path.startsWith(p)) ?? "", indexed: !/<meta name="robots" content="noindex">/.test(html) };
});
const byUrl = new Map(pages.map(page => [page.url, page]));

function tags(html, name) {
  return [...html.matchAll(new RegExp(`<${name}\\b([^>]*)>`, "gi"))].map(match =>
    Object.fromEntries([...match[1].matchAll(/([\w:-]+)(?:="([^"]*)")?/g)].map(([, key, value]) => [key.toLowerCase(), value ?? ""])));
}
const links = page => [...tags(page.html, "a"), ...tags(page.html, "link")].map(t => t.href).filter(Boolean);
const ids = page => new Set([...page.html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]));
// The page's schema.org nodes: every @graph of its JSON-LD scripts.
const structured = page => [...page.html.matchAll(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/g)]
  .flatMap(([, json]) => { const data = JSON.parse(json); return data["@graph"] ?? [data]; });

function target(url) {
  const { pathname, hash } = new URL(url, origin);
  return { pathname, fragment: decodeURIComponent(hash.slice(1)) };
}

test("the site has the expected pages in every language", () => {
  for (const prefix of Object.keys(languages))
    for (const name of ["", "help", "privacy"]) assert.ok(byUrl.has(`/${prefix}${name}`), `missing /${prefix}${name}`);
  assert.ok(byUrl.has("/404") && !byUrl.get("/404").indexed);
});

test("download links name the current release and are served by the download function", async () => {
  const bucket = {
    async head() { return { size: 1, uploaded: new Date(), etag: "e", httpEtag: '"e"' }; },
    async get() { throw new Error("HEAD requests must not read the object"); }
  };
  const files = new Set();
  for (const page of pages) for (const href of links(page)) {
    const { pathname } = target(href);
    if (!pathname.startsWith("/downloads/")) continue;
    assert.ok(href.startsWith("/downloads/") || href.startsWith(`${origin}/downloads/`), `${page.path}: ${href}`);
    const file = pathname.slice("/downloads/".length);
    assert.match(file, new RegExp(`^ClipDock-${version.replaceAll(".", "\\.")}-`), `${page.path} links an old release: ${file}`);
    files.add(file);
  }
  for (const file of releaseFiles) assert.ok(files.has(file), `the site never links ${file}`);
  for (const file of files) {
    const response = await onRequest({ request: new Request(`${origin}/downloads/${file}`, { method: "HEAD" }), env: { CLIPDOCK_RELEASES: bucket } });
    assert.equal(response.status, 200, `the download function does not serve ${file}`);
  }
});

// Each language shows (and shares) a screenshot of the app in that language.
test("every home page shows its own language's app screenshot, also as the page image of its language", () => {
  const screenshots = new Set();
  for (const prefix of Object.keys(languages)) {
    const figure = byUrl.get(`/${prefix}`).html.match(/<figure class="app-screen">[\s\S]*?<\/figure>/);
    assert.ok(figure, `/${prefix} has no app screenshot`);
    const image = tags(figure[0], "img")[0];
    screenshots.add(image.src);
    for (const name of ["", "help", "privacy"])
      assert.equal(tags(byUrl.get(`/${prefix}${name}`).html, "meta").find(t => t.property === "og:image")?.content, origin + image.src, `/${prefix}${name}`);
  }
  assert.equal(screenshots.size, Object.keys(languages).length, "two languages share one screenshot");
});

// The checksums are printed on each home page for copying instead of being linked as .sha256 downloads.
test("every home page shows one SHA-256 per release file, the same in every language", () => {
  let reference;
  for (const prefix of Object.keys(languages)) {
    const page = byUrl.get(`/${prefix}`);
    const shown = Object.fromEntries([...page.html.matchAll(/<code class="sha256" data-file="([^"]+)">([^<]*)<\/code>/g)].map(([, file, hash]) => [file, hash]));
    assert.deepEqual(Object.keys(shown).sort(), [...releaseFiles].sort(), `${page.path} shows checksums for ${Object.keys(shown).join(", ")}`);
    for (const [file, hash] of Object.entries(shown)) assert.match(hash, /^[0-9a-f]{64}$/, `${page.path}: ${file}`);
    reference ??= shown;
    assert.deepEqual(shown, reference, `${page.path} shows different checksums than the Japanese page`);
  }
});

// The apps update themselves from /updates/latest.json; it must announce the release the site offers.
test("the update manifest names the current release with the checksums shown on the home pages", async () => {
  const manifest = JSON.parse(readFileSync(join(root, "updates", "latest.json"), "utf8"));
  assert.equal(manifest.version, version, "updates/latest.json announces a different version");
  const shown = Object.fromEntries([...byUrl.get("/").html.matchAll(/<code class="sha256" data-file="([^"]+)">([^<]*)<\/code>/g)].map(([, file, hash]) => [file, hash]));
  const platforms = { "win-x64": releaseFiles[0], "mac-arm64": releaseFiles[1], "mac-x64": releaseFiles[2] };
  assert.deepEqual(Object.keys(manifest.files).sort(), Object.keys(platforms).sort());
  for (const [platform, file] of Object.entries(platforms)) {
    const entry = manifest.files[platform];
    assert.equal(entry.url, `/downloads/${file}`, platform);
    assert.equal(entry.sha256, shown[file], `${platform}: the manifest and the home page show different checksums`);
    assert.ok(Number.isSafeInteger(entry.size) && entry.size > 0, `${platform}: size`);
  }
  assert.match(readFileSync(join(root, "_headers"), "utf8"), /\/updates\/\*\r?\n\s+Cache-Control: no-cache/, "the manifest must not be cached");
});

test("version numbers outside the screenshot and its caption are the current release", () => {
  for (const page of pages) {
    const text = page.html.replace(/<figure\b[\s\S]*?<\/figure>/g, "").replace(/<script type="application\/ld\+json">[\s\S]*?<\/script>/g, "");
    for (const [found] of text.matchAll(/\b\d+\.\d+\.\d+\b/g)) assert.equal(found, version, `${page.path} mentions ${found}`);
    for (const software of structured(page).filter(node => node["@type"] === "SoftwareApplication")) {
      assert.equal(software.softwareVersion, version, page.path);
      assert.ok(software.downloadUrl.length === 3 && software.downloadUrl.every(link => link.includes(`/downloads/ClipDock-${version}-`)), page.path);
    }
  }
});

// Search engines and AI answers read the generated heads, structured data, sitemap and llms.txt.
test("page heads, structured data, the sitemap and llms.txt are generated and current", () => {
  const files = render(currentLastmod());
  for (const [file, contents] of Object.entries(files))
    // Line endings may differ by checkout (CRLF on Windows); only the content matters.
    assert.equal(readFileSync(file, "utf8").replaceAll("\r\n", "\n"), contents.replaceAll("\r\n", "\n"),
      `${relative(root, file)} is out of date; run node scripts/site-metadata.mjs`);
});

test("every indexable page has its own title and description and describes itself as structured data", () => {
  const indexed = pages.filter(p => p.indexed);
  const titles = indexed.map(p => p.html.match(/<title>([^<]*)<\/title>/)[1]);
  const descriptions = indexed.map(p => tags(p.html, "meta").find(t => t.name === "description").content);
  assert.equal(new Set(titles).size, indexed.length, "two pages share a title");
  assert.equal(new Set(descriptions).size, indexed.length, "two pages share a description");
  for (const [index, page] of indexed.entries()) {
    assert.ok(descriptions[index].length >= 50 && descriptions[index].length <= 320, `${page.path}: description length ${descriptions[index].length}`);
    const graph = structured(page);
    const webpage = graph.find(node => node["@type"] === "WebPage");
    assert.equal(webpage?.url, origin + page.url, `${page.path}: WebPage`);
    assert.equal(webpage.inLanguage, languages[page.prefix], page.path);
    const kinds = graph.map(node => node["@type"]);
    assert.ok(kinds.includes(page.url.endsWith("/") ? "SoftwareApplication" : "BreadcrumbList"), `${page.path}: ${kinds}`);
    // FAQ data must say exactly what the page shows; the generator reads it from the visible list.
    const faq = graph.find(node => node["@type"] === "FAQPage");
    const kind = page.url.endsWith("/") ? "index" : page.url.endsWith("help") ? "help" : "privacy";
    assert.deepEqual(faq?.mainEntity.map(q => [q.name, q.acceptedAnswer.text]) ?? [], visibleQuestions(page.html, kind), `${page.path}: FAQ`);
  }
});

test("robots.txt lets search engines and AI crawlers read the pages and names the sitemap", () => {
  const robots = readFileSync(join(root, "robots.txt"), "utf8");
  assert.ok(!/^Disallow:\s*\/\s*$/m.test(robots), "a crawler is blocked from the whole site");
  for (const agent of ["*", "GPTBot", "OAI-SearchBot", "ClaudeBot", "PerplexityBot", "Google-Extended"])
    assert.match(robots, new RegExp(`^User-agent: ${agent.replace("*", "\\*")}$`, "m"), agent);
  assert.match(robots, new RegExp(`^Sitemap: ${origin}/sitemap.xml$`, "m"));
});

test("internal links, assets and #fragments resolve to existing pages and ids", () => {
  for (const page of pages) {
    const references = [...links(page), ...tags(page.html, "img").map(t => t.src)].filter(Boolean);
    for (const reference of references) {
      if (/^(https?:)?\/\//.test(reference) && !reference.startsWith(origin)) continue;
      const { pathname, fragment } = target(reference.startsWith("#") ? page.url + reference : reference);
      if (pathname.startsWith("/downloads/")) continue;
      assert.ok(!pathname.endsWith(".html"), `${page.path}: link the pretty URL instead of ${reference}`);
      const linked = byUrl.get(pathname);
      if (!linked) {
        assert.ok(existsSync(join(root, pathname)), `${page.path}: ${reference} does not exist`);
        continue;
      }
      if (fragment) assert.ok(ids(linked).has(fragment), `${page.path}: ${reference} has no id="${fragment}"`);
    }
  }
});

test("translations keep the same sections, so links into any language find their anchors", () => {
  for (const name of ["", "help", "privacy"]) {
    const sections = Object.keys(languages).map(prefix => [...ids(byUrl.get(`/${prefix}${name}`))].sort().join(" "));
    assert.ok(sections.every(set => set === sections[0]), `/${name} has different ids per language:\n${sections.join("\n")}`);
  }
});

test("each page declares its language, canonical URL and every translation", () => {
  for (const page of pages.filter(p => p.indexed)) {
    assert.equal(tags(page.html, "html")[0].lang, languages[page.prefix], page.path);
    const canonical = tags(page.html, "link").find(t => t.rel === "canonical")?.href;
    assert.equal(canonical, origin + page.url, page.path);
    assert.equal(tags(page.html, "meta").find(t => t.property === "og:url")?.content, canonical, page.path);
    const name = page.url.slice(1 + page.prefix.length);
    const alternates = Object.fromEntries(tags(page.html, "link").filter(t => t.rel === "alternate").map(t => [t.hreflang, t.href]));
    const expected = Object.fromEntries(Object.entries(languages).map(([prefix, language]) => [language, `${origin}/${prefix}${name}`]));
    assert.deepEqual(alternates, { ...expected, "x-default": `${origin}/${name}` }, page.path);
    const current = tags(page.html, "a").filter(t => "aria-current" in t);
    assert.ok(current.length > 0 && current.every(t => t.href === page.url), `${page.path}: aria-current must mark links to this page only`);
    assert.ok(current.some(t => t.hreflang === languages[page.prefix]), `${page.path}: the language menu does not mark this language`);
    assert.equal((page.html.match(/<h1\b/g) || []).length, 1, `${page.path} needs exactly one h1`);
    assert.ok(tags(page.html, "meta").find(t => t.name === "description")?.content, `${page.path} has no description`);
  }
});

test("the sitemap lists every indexable page exactly once", () => {
  const sitemap = readFileSync(join(root, "sitemap.xml"), "utf8");
  const listed = [...sitemap.matchAll(/<loc>([^<]+)<\/loc>/g)].map(match => match[1]);
  assert.equal(new Set(listed).size, listed.length, "duplicate sitemap entries");
  assert.deepEqual(listed.sort(), pages.filter(p => p.indexed).map(p => origin + p.url).sort());
});

test("pages stay within the Content-Security-Policy in _headers", () => {
  const headers = readFileSync(join(root, "_headers"), "utf8");
  assert.match(headers, /Content-Security-Policy: default-src 'self'; style-src 'self'; img-src 'self';/);
  // Cloudflare Web Analytics adds its beacon to every page at deployment; nothing else may run scripts.
  assert.match(headers, /; script-src https:\/\/static\.cloudflareinsights\.com; connect-src 'self' https:\/\/cloudflareinsights\.com;/);
  for (const page of pages) {
    assert.ok(!/<style\b/i.test(page.html) && !/\sstyle="/i.test(page.html), `${page.path}: inline styles are blocked by style-src 'self'`);
    assert.ok(!/\son[a-z]+="/i.test(page.html), `${page.path}: inline event handlers are blocked`);
    for (const script of tags(page.html, "script")) assert.equal(script.type, "application/ld+json", `${page.path}: scripts are blocked by default-src 'self'`);
    for (const image of tags(page.html, "img")) {
      assert.ok(image.src.startsWith("/"), `${page.path}: img-src 'self' blocks ${image.src}`);
      assert.ok("alt" in image && image.width && image.height, `${page.path}: ${image.src} needs alt, width and height`);
    }
    for (const sheet of tags(page.html, "link").filter(t => t.rel === "stylesheet")) assert.ok(sheet.href.startsWith("/"), page.path);
  }
});
