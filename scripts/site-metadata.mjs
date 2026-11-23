// Writes the <head> of every website page and the sitemap from one place: titles, descriptions, language links,
// social previews and schema.org data (the app, its FAQ and breadcrumbs) for search engines and AI answers.
// The FAQ data is read from the visible FAQ, so the two never disagree.
//
//   node scripts/site-metadata.mjs            rewrite the pages and website/sitemap.xml (lastmod = today)
//   node scripts/site-metadata.mjs --check    exit 1 when a page or the sitemap is out of date (used by npm test)
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

export const origin = "https://clipdock-d6z.pages.dev";
const root = fileURLToPath(new URL("../website/", import.meta.url));
const version = readFileSync(new URL("../Directory.Build.props", import.meta.url), "utf8").match(/<Version>(\d+\.\d+\.\d+)<\/Version>/)[1];
const screenshot = { width: 1186, height: 813 };

const languages = [
  { prefix: "", lang: "ja", locale: "ja_JP", image: "/assets/clipdock-desktop.jpg" },
  { prefix: "en/", lang: "en", locale: "en_US", image: "/assets/clipdock-desktop-en.jpg" },
  { prefix: "zh-cn/", lang: "zh-Hans", locale: "zh_CN", image: "/assets/clipdock-desktop-zh-cn.jpg" },
  { prefix: "es/", lang: "es", locale: "es_ES", image: "/assets/clipdock-desktop-es.jpg" }
];

const text = {
  ja: {
    home: "ホーム", help: "使い方・ヘルプ", privacy: "プライバシーポリシー", imageAlt: "ClipDockのWindowsアプリ画面",
    pages: {
      index: ["ClipDock — 動画をMP4、音声をMP3で保存できる無料アプリ（Windows・Mac）",
        "ClipDockは、動画ページのURLを貼り付けるだけで、動画をMP4、音声をMP3としてWindows・Macに保存できる無料アプリです。画質の上限・保存先・同時保存数を選べ、自動アップデートと4言語表示に対応しています。"],
      help: ["ClipDockの使い方・ヘルプ — インストール、MP4・MP3保存、自動アップデート",
        "ClipDockのインストール方法、Windows・Macで警告が出たときの確認、URLからMP4・MP3を保存する手順、画質と保存形式、ログインが必要な動画、常駐と自動アップデート、保存できないときの対処を説明します。"],
      privacy: ["プライバシーポリシー — ClipDock",
        "ClipDockが扱う情報（保存したURL・履歴・ログイン情報）とPC内の保存場所、通信先、公式サイトのアクセス解析について説明します。アプリは利用状況を送信しません。"]
    },
    features: ["URLから動画をMP4、音声をMP3で保存", "画質の上限（480p〜4K・最高画質）と保存先を選択", "1〜4件の同時保存と進捗表示",
      "ログインが必要なサイトに専用画面でサインイン", "通知領域・メニューバーで常駐し、サインイン時に自動起動", "アプリと保存エンジンの自動アップデート", "日本語・English・简体中文・Español"],
    requirements: "Windows 10 / 11（64bit、x64）、またはmacOS 14以降（Appleシリコン・Intel）。初回の起動時にインターネット接続が必要です。"
  },
  en: {
    home: "Home", help: "Help", privacy: "Privacy policy", imageAlt: "ClipDock for Windows in English",
    pages: {
      index: ["ClipDock — Free MP4 and MP3 saver for Windows and Mac",
        "ClipDock is a free Windows and Mac app that saves video as MP4 and audio as MP3 from a URL you paste. Choose a quality limit, folder and number of parallel downloads; it updates itself and speaks four languages."],
      help: ["ClipDock help — install, save MP4 or MP3, automatic updates",
        "How to install ClipDock, check Windows and Mac warnings, save MP4 or MP3 from a URL, pick quality and format, save videos that need a login, run in the background with automatic updates, and fix failed downloads."],
      privacy: ["Privacy policy — ClipDock",
        "What ClipDock handles (saved URLs, history, login data), where it is stored on your computer, which services it connects to, and how the website measures visits. The app sends no usage data."]
    },
    features: ["Save video as MP4 and audio as MP3 from a URL", "Choose a quality limit (480p to 4K or best) and a folder", "One to four downloads at once with progress",
      "Sign in to sites that need a login in a dedicated window", "Runs in the notification area or menu bar and starts at sign-in", "Automatic updates for the app and the download engine", "English, Japanese, Simplified Chinese and Spanish"],
    requirements: "Windows 10 or 11 (64-bit x64), or macOS 14 or later (Apple silicon or Intel). An internet connection is needed on first launch."
  },
  "zh-Hans": {
    home: "首页", help: "帮助", privacy: "隐私政策", imageAlt: "ClipDock Windows 应用界面（简体中文）",
    pages: {
      index: ["ClipDock — 免费将视频保存为 MP4、音频保存为 MP3 的应用（Windows、Mac）",
        "ClipDock 是一款免费的 Windows 和 Mac 应用，粘贴视频网址即可将视频保存为 MP4、音频保存为 MP3。可设置画质上限、保存位置和同时下载数，支持自动更新和四种界面语言。"],
      help: ["ClipDock 帮助 — 安装、保存 MP4 或 MP3、自动更新",
        "介绍 ClipDock 的安装方法、Windows 和 Mac 显示警告时如何确认、从网址保存 MP4 或 MP3 的步骤、画质与格式、需要登录的视频、后台运行与自动更新，以及无法保存时的处理方法。"],
      privacy: ["隐私政策 — ClipDock",
        "说明 ClipDock 处理的信息（保存的网址、历史记录、登录数据）、在电脑中的保存位置、连接的服务，以及官网如何统计访问。应用不会发送使用情况数据。"]
    },
    features: ["从网址将视频保存为 MP4、音频保存为 MP3", "选择画质上限（480p 至 4K 或最高画质）和保存位置", "同时下载 1 至 4 项并显示进度",
      "在专用窗口中登录需要账号的网站", "在通知区域或菜单栏后台运行，登录时自动启动", "应用和下载引擎自动更新", "简体中文、英语、日语和西班牙语界面"],
    requirements: "Windows 10 或 11（64 位 x64），或 macOS 14 及以上（Apple 芯片或 Intel）。首次启动时需要联网。"
  },
  es: {
    home: "Inicio", help: "Ayuda", privacy: "Política de privacidad", imageAlt: "ClipDock para Windows en español",
    pages: {
      index: ["ClipDock — Guarda vídeo en MP4 y audio en MP3 gratis (Windows, Mac)",
        "ClipDock es una aplicación gratuita para Windows y Mac que guarda vídeo en MP4 y audio en MP3 a partir de una URL. Elige el límite de calidad, la carpeta y las descargas simultáneas; se actualiza sola y está en cuatro idiomas."],
      help: ["Ayuda de ClipDock — instalar, guardar MP4 o MP3, actualizaciones automáticas",
        "Cómo instalar ClipDock, revisar las advertencias de Windows y Mac, guardar MP4 o MP3 desde una URL, elegir calidad y formato, guardar vídeos que requieren sesión, usarlo en segundo plano con actualizaciones automáticas y resolver fallos."],
      privacy: ["Política de privacidad — ClipDock",
        "Qué datos maneja ClipDock (URL guardadas, historial, datos de acceso), dónde se guardan en tu ordenador, a qué servicios se conecta y cómo mide las visitas el sitio. La aplicación no envía datos de uso."]
    },
    features: ["Guarda vídeo en MP4 y audio en MP3 desde una URL", "Elige el límite de calidad (de 480p a 4K o la mejor) y la carpeta", "De una a cuatro descargas a la vez con progreso",
      "Inicia sesión en sitios que lo requieren desde una ventana propia", "Se ejecuta en el área de notificación o la barra de menús y se inicia con la sesión",
      "Actualizaciones automáticas de la aplicación y del motor", "Español, inglés, japonés y chino simplificado"],
    requirements: "Windows 10 u 11 (64 bits x64), o macOS 14 o posterior (Apple silicon o Intel). Se necesita conexión a internet en el primer inicio."
  }
};

const pageNames = ["index", "help", "privacy"];
const url = (language, page) => `${origin}/${language.prefix}${page === "index" ? "" : page}`;
const attribute = value => value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
const plain = html => html.replace(/<[^>]+>/g, "").replaceAll("&lt;", "<").replaceAll("&gt;", ">").replaceAll("&quot;", '"')
  .replaceAll("&#39;", "'").replaceAll("&amp;", "&").replace(/\s+/g, " ").trim();

// The questions and answers people see: the home page FAQ, and the help page's troubleshooting list.
export function visibleQuestions(html, page) {
  const list = page === "index" ? html.match(/<dl class="faq-list">([\s\S]*?)<\/dl>/)
    : page === "help" ? html.match(/<section id="troubleshooting">[\s\S]*?<dl class="answers">([\s\S]*?)<\/dl>/) : null;
  if (!list) return [];
  return [...list[1].matchAll(/<dt>([\s\S]*?)<\/dt><dd>([\s\S]*?)<\/dd>/g)].map(([, question, answer]) => [plain(question), plain(answer)]);
}

function structuredData(language, page, html) {
  const t = text[language.lang];
  const [title, description] = t.pages[page];
  const canonical = url(language, page);
  const home = url(language, "index");
  const organization = { "@type": "Organization", "@id": `${origin}/#organization`, name: "ClipDock", url: `${origin}/`,
    logo: { "@type": "ImageObject", url: `${origin}/assets/clipdock-icon.png`, width: 128, height: 128 } };
  const website = { "@type": "WebSite", "@id": `${origin}/#website`, url: `${origin}/`, name: "ClipDock", inLanguage: languages.map(l => l.lang),
    publisher: { "@id": `${origin}/#organization` } };
  const image = { "@type": "ImageObject", url: origin + language.image, width: screenshot.width, height: screenshot.height, caption: t.imageAlt };
  const webpage = { "@type": "WebPage", "@id": `${canonical}#webpage`, url: canonical, name: title, description, inLanguage: language.lang,
    isPartOf: { "@id": `${origin}/#website` } };
  const graph = [organization, website, webpage];
  if (page === "index") {
    const software = { "@type": "SoftwareApplication", "@id": `${canonical}#software`, name: "ClipDock", url: canonical, description,
      applicationCategory: "MultimediaApplication", operatingSystem: "Windows 10, Windows 11, macOS 14 or later", softwareVersion: version,
      downloadUrl: ["win-x64-unsigned-setup.exe", "mac-arm64-unsigned.dmg", "mac-x64-unsigned.dmg"].map(file => `${origin}/downloads/ClipDock-${version}-${file}`),
      installUrl: `${canonical}#install`, screenshot: image, image, featureList: t.features, softwareRequirements: t.requirements,
      inLanguage: languages.map(l => l.lang), isAccessibleForFree: true, license: "https://opensource.org/license/mit",
      offers: { "@type": "Offer", price: "0", priceCurrency: "USD", availability: "https://schema.org/InStock", url: canonical },
      author: { "@id": `${origin}/#organization` }, publisher: { "@id": `${origin}/#organization` } };
    webpage.primaryImageOfPage = image;
    webpage.about = { "@id": software["@id"] };
    graph.push(software);
  } else {
    const breadcrumb = { "@type": "BreadcrumbList", "@id": `${canonical}#breadcrumb`, itemListElement: [
      { "@type": "ListItem", position: 1, name: t.home, item: home },
      { "@type": "ListItem", position: 2, name: t[page], item: canonical }] };
    webpage.breadcrumb = { "@id": breadcrumb["@id"] };
    graph.push(breadcrumb);
  }
  const questions = visibleQuestions(html, page);
  if (questions.length) graph.push({ "@type": "FAQPage", "@id": `${canonical}#faq`, url: canonical, inLanguage: language.lang,
    isPartOf: { "@id": `${canonical}#webpage` },
    mainEntity: questions.map(([name, answer]) => ({ "@type": "Question", name, acceptedAnswer: { "@type": "Answer", text: answer } })) });
  return JSON.stringify({ "@context": "https://schema.org", "@graph": graph }).replaceAll("</", "<\\/");
}

function head(language, page, html) {
  const t = text[language.lang];
  const [title, description] = t.pages[page];
  const canonical = url(language, page);
  const image = origin + language.image;
  return [
    '<meta charset="utf-8">',
    '<meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta name="theme-color" content="#ffffff">',
    `<title>${attribute(title)}</title>`,
    `<meta name="description" content="${attribute(description)}">`,
    '<meta name="robots" content="index, follow, max-image-preview:large, max-snippet:-1, max-video-preview:-1">',
    `<link rel="canonical" href="${canonical}">`,
    ...languages.map(l => `<link rel="alternate" hreflang="${l.lang}" href="${url(l, page)}">`),
    `<link rel="alternate" hreflang="x-default" href="${url(languages[0], page)}">`,
    '<link rel="icon" href="/assets/favicon.png" type="image/png" sizes="32x32">',
    '<link rel="apple-touch-icon" href="/assets/clipdock-icon.png">',
    '<link rel="stylesheet" href="/styles.css">',
    '<meta property="og:type" content="website">',
    '<meta property="og:site_name" content="ClipDock">',
    `<meta property="og:title" content="${attribute(title)}">`,
    `<meta property="og:description" content="${attribute(description)}">`,
    `<meta property="og:url" content="${canonical}">`,
    `<meta property="og:image" content="${image}">`,
    `<meta property="og:image:width" content="${screenshot.width}">`,
    `<meta property="og:image:height" content="${screenshot.height}">`,
    `<meta property="og:image:alt" content="${attribute(t.imageAlt)}">`,
    `<meta property="og:locale" content="${language.locale}">`,
    ...languages.filter(l => l !== language).map(l => `<meta property="og:locale:alternate" content="${l.locale}">`),
    '<meta name="twitter:card" content="summary_large_image">',
    `<meta name="twitter:title" content="${attribute(title)}">`,
    `<meta name="twitter:description" content="${attribute(description)}">`,
    `<meta name="twitter:image" content="${image}">`,
    `<meta name="twitter:image:alt" content="${attribute(t.imageAlt)}">`,
    `<script type="application/ld+json">${structuredData(language, page, html)}</script>`
  ].join("\n");
}

function sitemap(lastmod) {
  const entries = languages.flatMap(language => pageNames.map(page => [
    "  <url>",
    `    <loc>${url(language, page)}</loc>`,
    `    <lastmod>${lastmod(url(language, page))}</lastmod>`,
    ...languages.map(l => `    <xhtml:link rel="alternate" hreflang="${l.lang}" href="${url(l, page)}"/>`),
    `    <xhtml:link rel="alternate" hreflang="x-default" href="${url(languages[0], page)}"/>`,
    ...(page === "index" ? [`    <image:image><image:loc>${origin}${language.image}</image:loc></image:image>`] : []),
    "  </url>"].join("\n")));
  return ['<?xml version="1.0" encoding="UTF-8"?>',
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml" xmlns:image="http://www.google.com/schemas/sitemap-image/1.1">',
    ...entries, "</urlset>", ""].join("\n");
}

// /llms.txt (llmstxt.org): a short, factual summary with links, for AI assistants that answer questions about ClipDock.
function llms() {
  return `# ClipDock

> ClipDock is a free desktop app for Windows 10/11 (x64) and macOS 14 or later (Apple silicon and Intel) that saves video as MP4 and audio as MP3 from a URL you paste. Current version: ${version}.

- Price: free, with no ads and no account. License: MIT.
- How it works: paste the URL of a video page or media file, choose MP4 (with a quality limit from 480p to 4K, or best) or MP3 and a folder, then click Save. One to four downloads run at once.
- Engine: yt-dlp and FFmpeg, which ClipDock downloads on first launch and keeps up to date.
- Scope: sites supported by yt-dlp and non-DRM HLS/DASH media. ClipDock does not remove DRM or bypass access restrictions; use it only for media you have the right to save.
- Videos that need a login: sign in inside ClipDock's own login window, separate from your browser.
- Background and updates: by default ClipDock starts at sign-in and waits in the Windows notification area or the Mac menu bar, and it updates itself (from 1.0.6 on, after a size and SHA-256 check).
- Interface languages: English, Japanese, Simplified Chinese and Spanish.
- Privacy: the app sends no usage data or telemetry. The website counts visits with cookieless Cloudflare Web Analytics.
- Trust: the Windows installer is unsigned and the Mac app is not notarized, so the systems show a warning on first install; the help pages explain how to check the download. SHA-256 checksums are printed on the home page.

## Download and overview

- [ClipDock (English)](${origin}/en/): downloads for Windows and Mac, SHA-256 checksums and FAQ
- [ClipDock（日本語）](${origin}/)
- [ClipDock（简体中文）](${origin}/zh-cn/)
- [ClipDock (Español)](${origin}/es/)

## Help

- [Help (English)](${origin}/en/help): installation, system warnings, saving from a URL, quality and formats, login, running in the background, automatic updates, troubleshooting and uninstalling
- [使い方・ヘルプ（日本語）](${origin}/help)
- [帮助（简体中文）](${origin}/zh-cn/help)
- [Ayuda (Español)](${origin}/es/help)

## Policies

- [Privacy policy (English)](${origin}/en/privacy): what the app stores and where, network connections, website analytics
- [プライバシーポリシー（日本語）](${origin}/privacy)

## Optional

- [Update manifest](${origin}/updates/latest.json): current version, download URLs, sizes and SHA-256 for each platform
`;
}

// Every generated file as it should be: { path: contents }. lastmod(url) gives each sitemap date.
export function render(lastmod) {
  const files = {};
  for (const language of languages) for (const page of pageNames) {
    const file = join(root, language.prefix, `${page}.html`);
    const html = readFileSync(file, "utf8");
    const start = html.indexOf("<head>") + "<head>".length;
    const end = html.indexOf("</head>");
    if (start < "<head>".length || end < start) throw new Error(`${file} has no <head>`);
    const newline = html.includes("\r\n") ? "\r\n" : "\n";
    files[file] = html.slice(0, start) + newline + head(language, page, html).replaceAll("\n", newline) + newline + html.slice(end);
  }
  files[join(root, "sitemap.xml")] = sitemap(lastmod);
  files[join(root, "llms.txt")] = llms();
  return files;
}

// The sitemap's current dates, so a check ignores when pages were last regenerated.
export function currentLastmod() {
  const sitemapText = readFileSync(join(root, "sitemap.xml"), "utf8");
  const dates = Object.fromEntries([...sitemapText.matchAll(/<loc>([^<]+)<\/loc>\s*<lastmod>([^<]+)<\/lastmod>/g)].map(([, loc, date]) => [loc, date]));
  return loc => dates[loc] ?? "missing";
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const check = process.argv.includes("--check");
  const today = new Date().toISOString().slice(0, 10);
  const files = render(check ? currentLastmod() : () => today);
  const current = file => { try { return readFileSync(file, "utf8"); } catch { return null; } };
  // Line endings may differ by checkout (CRLF on Windows); only the content matters.
  const same = (a, b) => a !== null && a.replaceAll("\r\n", "\n") === b.replaceAll("\r\n", "\n");
  const stale = Object.entries(files).filter(([file, contents]) => !same(current(file), contents)).map(([file]) => file);
  if (check) {
    if (stale.length) { console.error(`Out of date; run node scripts/site-metadata.mjs:\n${stale.join("\n")}`); process.exit(1); }
    console.log("Website metadata is up to date.");
  } else {
    for (const [file, contents] of Object.entries(files)) writeFileSync(file, contents);
    console.log(`Wrote ${Object.keys(files).length} files (${stale.length} changed).`);
  }
}
