// Tells search engines that take IndexNow (Bing, which also feeds Copilot and ChatGPT search, Yandex, Seznam,
// Naver and others) that the site's pages changed, so they are crawled again soon. Run after deploying the site:
//   npm run notify:search
// The key is public by design: it only proves that whoever submits can publish files on the site.
import { readFileSync } from "node:fs";

const origin = "https://clipdock-d6z.pages.dev";
const key = readFileSync(new URL("../website/indexnow.txt", import.meta.url), "utf8").trim();
const sitemap = readFileSync(new URL("../website/sitemap.xml", import.meta.url), "utf8");
const urlList = [...sitemap.matchAll(/<loc>([^<]+)<\/loc>/g)].map(match => match[1]);
if (!/^[0-9a-f]{32}$/.test(key) || urlList.length === 0) throw new Error("Missing IndexNow key or sitemap URLs.");

// The engines fetch the key from the live site, so it must be deployed first.
const published = await fetch(`${origin}/indexnow.txt`, { cache: "no-store" });
if (!published.ok || (await published.text()).trim() !== key) throw new Error("Deploy the site first: the live indexnow.txt does not match.");

const response = await fetch("https://api.indexnow.org/indexnow", {
  method: "POST",
  headers: { "Content-Type": "application/json; charset=utf-8" },
  body: JSON.stringify({ host: new URL(origin).host, key, keyLocation: `${origin}/indexnow.txt`, urlList })
});
console.log(`IndexNow: ${response.status} ${response.statusText} for ${urlList.length} URLs`);
if (!response.ok) process.exit(1);
