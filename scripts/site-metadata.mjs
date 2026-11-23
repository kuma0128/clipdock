// Writes the <head> of every website page, the sitemap and the files for AI assistants from one place: titles,
// descriptions, language links, social previews and schema.org data (the app, its FAQ and breadcrumbs) for search
// engines and AI answers, /llms.txt (a short factual summary) and /llms-full.txt (the English pages as Markdown).
// The FAQ data is read from the visible FAQ, so the two never disagree.
//
//   node scripts/site-metadata.mjs            rewrite the generated files; a page whose content changed is dated today
//   node scripts/site-metadata.mjs --check    exit 1 when a generated file is out of date (used by npm test)
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join } from "node:path";

export const origin = "https://clipdock-d6z.pages.dev";
const root = fileURLToPath(new URL("../website/", import.meta.url));
const version = readFileSync(new URL("../Directory.Build.props", import.meta.url), "utf8").match(/<Version>(\d+\.\d+\.\d+)<\/Version>/)[1];
const screenshot = { width: 1186, height: 813 };
// Ownership proofs for webmaster tools. They are public by design; removing one ends that tool's access.
const verification = [["google-site-verification", "FQT_edwaiw8ZB0ohzxScJlazAQRKzzdjQ_XNJiiWWQk"],
  ["msvalidate.01", "96E8B5FA571AC413CD5AF5C83DAA733A"]];

const languages = [
  { prefix: "", lang: "ja", locale: "ja_JP", image: "/assets/clipdock-desktop.jpg" },
  { prefix: "en/", lang: "en", locale: "en_US", image: "/assets/clipdock-desktop-en.jpg" },
  { prefix: "zh-cn/", lang: "zh-Hans", locale: "zh_CN", image: "/assets/clipdock-desktop-zh-cn.jpg" },
  { prefix: "es/", lang: "es", locale: "es_ES", image: "/assets/clipdock-desktop-es.jpg" }
];
// People whose language has no page here are sent to the English one (hreflang x-default), not the Japanese one.
const fallback = languages.find(language => language.lang === "en");

// Titles and descriptions use the words people search with ("video downloader", 動画ダウンロード, 视频下载,
// descargador de vídeos) next to what ClipDock does. imageAlt describes the screenshot; the pages use it as its alt text.
// disambiguation keeps search engines and AI answers from mixing ClipDock up with other products of the same name.
const text = {
  ja: {
    home: "ホーム", help: "使い方・ヘルプ", privacy: "プライバシーポリシー",
    imageAlt: "ClipDockのWindowsアプリ画面。URLの入力欄、MP4・MP3と画質の選択、保存ボタン、保存先、同時保存数が並んでいる",
    alternateName: "クリップドック", category: "動画ダウンローダー",
    disambiguation: "このサイトで配布しているWindows・Mac用のデスクトップアプリです。ClipDockという名前の別のアプリやWebサービスとは関係ありません。",
    pages: {
      index: ["ClipDock — MP4・MP3で保存できる無料の動画ダウンロードアプリ（Windows・Mac）",
        "ClipDock（クリップドック）は、動画ページのURLを貼り付けるだけで、動画をMP4、音声をMP3としてパソコンに保存できる無料の動画ダウンロードソフトです。Windows 10 / 11とmacOS 14以降に対応し、広告・登録なし。画質の上限（480p〜4K、最高画質）と保存先を選べ、自動でアップデートします。"],
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
    home: "Home", help: "Help", privacy: "Privacy policy",
    imageAlt: "ClipDock for Windows in English, showing the URL box, the MP4/MP3 and quality menus, the Save button, the destination folder and the parallel-download setting",
    category: "Video downloader",
    disambiguation: "The Windows and Mac desktop app published on this site. It is not related to other apps or web services that are also called ClipDock.",
    pages: {
      index: ["ClipDock — Free video downloader for Windows and Mac (MP4, MP3)",
        "ClipDock is a free video downloader for Windows 10/11 and macOS 14 or later. Paste a URL to save video as MP4 or audio as MP3, and choose a quality limit (480p to 4K, or best) and a folder. No ads, no account, automatic updates."],
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
    home: "首页", help: "帮助", privacy: "隐私政策",
    imageAlt: "ClipDock Windows 应用界面（简体中文），显示网址输入框、MP4/MP3 和画质选择、保存按钮、保存位置和同时下载数",
    category: "视频下载器",
    disambiguation: "在本网站发布的 Windows 和 Mac 桌面应用，与其他同样名为 ClipDock 的应用或网络服务无关。",
    pages: {
      index: ["ClipDock — 免费视频下载软件，保存为 MP4 或 MP3（Windows、Mac）",
        "ClipDock 是一款免费的视频下载软件，支持 Windows 10/11 和 macOS 14 及以上版本。粘贴视频网址即可将视频保存为 MP4、音频保存为 MP3，并可选择画质上限（480p 至 4K 或最高画质）和保存位置。无广告、无需注册，自动更新。"],
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
    home: "Inicio", help: "Ayuda", privacy: "Política de privacidad",
    imageAlt: "ClipDock para Windows en español, con el cuadro de URL, los menús de MP4/MP3 y calidad, el botón Guardar, la carpeta de destino y las descargas simultáneas",
    category: "Descargador de vídeos",
    disambiguation: "La aplicación de escritorio para Windows y Mac que se publica en este sitio. No tiene relación con otras aplicaciones o servicios web que también se llaman ClipDock.",
    pages: {
      index: ["ClipDock — Descargador de vídeos gratis para Windows y Mac (MP4 y MP3)",
        "ClipDock es un descargador de vídeos gratuito para Windows 10/11 y macOS 14 o posterior. Pega una URL para guardar vídeo en MP4 o audio en MP3 y elige el límite de calidad (de 480p a 4K, o la mejor) y la carpeta. Sin anuncios ni registro, con actualizaciones automáticas."],
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
const pageFile = (language, page) => join(root, language.prefix, `${page}.html`);
const attribute = value => value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
const decode = html => html.replaceAll("&lt;", "<").replaceAll("&gt;", ">").replaceAll("&quot;", '"').replaceAll("&#39;", "'")
  .replaceAll("&nbsp;", " ").replaceAll("&amp;", "&");
const plain = html => decode(html.replace(/<[^>]+>/g, "")).replace(/\s+/g, " ").trim();
// Line endings may differ by checkout (CRLF on Windows); only the content matters.
const same = (a, b) => a !== null && a.replaceAll("\r\n", "\n") === b.replaceAll("\r\n", "\n");

// The questions and answers people see: the home page FAQ, and the help page's troubleshooting list.
export function visibleQuestions(html, page) {
  const list = page === "index" ? html.match(/<dl class="faq-list">([\s\S]*?)<\/dl>/)
    : page === "help" ? html.match(/<section id="troubleshooting">[\s\S]*?<dl class="answers">([\s\S]*?)<\/dl>/) : null;
  if (!list) return [];
  return [...list[1].matchAll(/<dt>([\s\S]*?)<\/dt><dd>([\s\S]*?)<\/dd>/g)].map(([, question, answer]) => [plain(question), plain(answer)]);
}

function structuredData(language, page, html, date) {
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
    isPartOf: { "@id": `${origin}/#website` }, dateModified: date };
  const graph = [organization, website, webpage];
  if (page === "index") {
    const software = { "@type": "SoftwareApplication", "@id": `${canonical}#software`, name: "ClipDock",
      ...(t.alternateName ? { alternateName: t.alternateName } : {}), url: canonical, description, disambiguatingDescription: t.disambiguation,
      applicationCategory: "MultimediaApplication", applicationSubCategory: t.category,
      operatingSystem: "Windows 10, Windows 11, macOS 14 or later", softwareVersion: version,
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

function head(language, page, html, date) {
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
    ...verification.map(([name, content]) => `<meta name="${name}" content="${content}">`),
    `<link rel="canonical" href="${canonical}">`,
    ...languages.map(l => `<link rel="alternate" hreflang="${l.lang}" href="${url(l, page)}">`),
    `<link rel="alternate" hreflang="x-default" href="${url(fallback, page)}">`,
    '<link rel="icon" href="/assets/favicon.png" type="image/png" sizes="32x32">',
    // Search results show a site's icon; they prefer one larger than 48 pixels.
    '<link rel="icon" href="/assets/clipdock-icon.png" type="image/png" sizes="128x128">',
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
    `<script type="application/ld+json">${structuredData(language, page, html, date)}</script>`
  ].join("\n");
}

function withHead(language, page, html, date) {
  const start = html.indexOf("<head>") + "<head>".length;
  const end = html.indexOf("</head>");
  if (start < "<head>".length || end < start) throw new Error(`${pageFile(language, page)} has no <head>`);
  const newline = html.includes("\r\n") ? "\r\n" : "\n";
  return html.slice(0, start) + newline + head(language, page, html, date).replaceAll("\n", newline) + newline + html.slice(end);
}

function sitemap(dates) {
  const entries = languages.flatMap(language => pageNames.map(page => [
    "  <url>",
    `    <loc>${url(language, page)}</loc>`,
    `    <lastmod>${dates[url(language, page)]}</lastmod>`,
    ...languages.map(l => `    <xhtml:link rel="alternate" hreflang="${l.lang}" href="${url(l, page)}"/>`),
    `    <xhtml:link rel="alternate" hreflang="x-default" href="${url(fallback, page)}"/>`,
    ...(page === "index" ? [`    <image:image><image:loc>${origin}${language.image}</image:loc></image:image>`] : []),
    "  </url>"].join("\n")));
  return ['<?xml version="1.0" encoding="UTF-8"?>',
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml" xmlns:image="http://www.google.com/schemas/sitemap-image/1.1">',
    ...entries, "</urlset>", ""].join("\n");
}

// /llms.txt (llmstxt.org): a short, factual summary with links, for AI assistants that answer questions about ClipDock.
function llms() {
  return `# ClipDock

> ClipDock is a free desktop app for Windows 10/11 (x64) and macOS 14 or later (Apple silicon and Intel) that saves video as MP4 and audio as MP3 from a URL you paste. Official website: ${origin}/ · Current version: ${version}.

Which ClipDock this is: the Windows and Mac video and audio saver published at ${origin}/ (note the "-d6z" in the address). Other apps, websites and services that are also called ClipDock, such as the web-based video downloader at clipdock.pages.dev, clipboard managers and iPhone apps, are separate products by other developers and are not related to it.

## Key facts

- What it does: paste the URL of a video page or media file, choose MP4 (with a quality limit from 480p to 4K, or best) or MP3 and a folder, then click Save. One to four downloads run at once (two by default).
- Price: free, with no ads and no account. License: MIT.
- Platforms: Windows 10/11 (64-bit x64) and macOS 14 or later (Apple silicon or Intel). There is no iPhone, Android or Linux version, and ClipDock is neither a website nor a browser extension.
- Engine: yt-dlp and FFmpeg, which ClipDock downloads on first launch and keeps up to date. .NET and Python do not need to be installed.
- Scope: sites supported by yt-dlp and non-DRM HLS/DASH media. ClipDock does not remove DRM or bypass access restrictions; use it only for media you have the right to save.
- Videos that need a login: sign in inside ClipDock's own login window, separate from your browser. ClipDock does not read your browser's cookies or passwords.
- Saved files: by default in a "ClipDock" folder inside Downloads; another folder can be chosen.
- Background and updates: by default ClipDock starts at sign-in and waits in the Windows notification area or the Mac menu bar. From 1.0.6 on it updates itself, after checking the size and SHA-256 of the new version against this site.
- Interface languages: English, Japanese, Simplified Chinese and Spanish. On first launch the app uses the computer's display language when it is one of these (on a Mac, the first of them in the preferred languages) and English otherwise, Traditional Chinese included; choose another language under "Language (after restart)" at the bottom left and reopen the app.
- Privacy: the app sends no usage data or telemetry. The website counts visits with cookieless Cloudflare Web Analytics.
- Safety: the Windows installer is not code-signed and the Mac app is not notarized, so Windows and macOS show a warning on first install. Download only from the official website and compare the SHA-256 checksums printed on its home page; the help pages explain how to check the warning.

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
- [隐私政策（简体中文）](${origin}/zh-cn/privacy)
- [Política de privacidad (Español)](${origin}/es/privacy)

## Optional

- [Full text (English)](${origin}/llms-full.txt): the English home, help and privacy pages as Markdown in one file
- [Update manifest](${origin}/updates/latest.json): current version, download URLs, sizes and SHA-256 for each platform
`;
}

const headingPrefix = { h1: "# ", h2: "## ", h3: "### " };
const blockElements = new Set(["main", "section", "article", "aside", "header", "footer", "nav", "div", "p", "h1", "h2", "h3",
  "ol", "ul", "li", "dl", "dt", "dd", "figure", "figcaption"]);

// The main content of a page as Markdown: headings, paragraphs, lists, definition lists, tables, links and code,
// the elements the pages are written with. The page's table of contents, the screenshot and the repeated download
// buttons at the bottom are left out. Links become absolute so the text stands on its own.
export function markdown(html, address) {
  const main = html.match(/<main\b[^>]*>([\s\S]*?)<\/main>/)[1]
    .replace(/<aside class="document-nav">[\s\S]*?<\/aside>/g, "")
    .replace(/<figure\b[\s\S]*?<\/figure>/g, "")
    .replace(/<div class="bottom-download">[\s\S]*?<\/div><\/div>/g, "");
  const blocks = [];
  const lists = [];
  let buffer = "", prefix = "", suffix = "", kind = "p", href = null, button = false, table = null, row = null;
  const flush = () => {
    const value = buffer.replace(/\s+/g, " ").trim();
    if (value) blocks.push([kind, prefix + value + suffix]);
    buffer = ""; prefix = ""; suffix = ""; kind = "p";
  };
  const inline = (name, closing, attributes) => {
    if (name === "code") return "`";
    if (name === "strong" || name === "b") return "**";
    if (name === "br") return " ";
    if (name === "small") return closing ? ")" : " (";
    if (name !== "a") return "";
    if (!closing) {
      const target = attributes.match(/\bhref="([^"]*)"/);
      href = target ? new URL(decode(target[1]), address).href : null;
      return href ? "[" : "";
    }
    return href ? `](${href})` : "";
  };
  for (const token of main.split(/(<[^>]+>)/)) {
    const tag = token.match(/^<(\/?)([a-zA-Z0-9]+)\b([^>]*)>$/);
    if (!tag) { buffer += decode(token); continue; }
    const [, closing, name, attributes] = [tag[0], tag[1] === "/", tag[2].toLowerCase(), tag[3]];
    if (table) {
      if (name === "table" && closing) {
        const cells = cellsRow => `| ${cellsRow.map(cell => cell.replaceAll("|", "\\|")).join(" | ")} |`;
        blocks.push(["table", [cells(table[0]), cells(table[0].map(() => "---")), ...table.slice(1).map(cells)].join("\n")]);
        table = null;
      } else if (name === "tr") { if (closing) table.push(row); else row = []; }
      else if (name === "th" || name === "td") { if (closing) row.push(buffer.replace(/\s+/g, " ").trim()); buffer = ""; }
      else buffer += inline(name, closing, attributes);
      continue;
    }
    if (name === "table") { flush(); table = []; continue; }
    if (blockElements.has(name)) {
      flush();
      if (closing) { if (name === "ol" || name === "ul") lists.pop(); continue; }
      if (name === "ol" || name === "ul") lists.push(name === "ol" ? 0 : null);
      else if (name === "li") { kind = "li"; prefix = lists.at(-1) === null ? "- " : `${lists[lists.length - 1] += 1}. `; }
      else if (name === "dt") { prefix = "**"; suffix = "**"; }
      else prefix = headingPrefix[name] ?? "";
      continue;
    }
    // The download buttons are links side by side; each becomes a list item of its own.
    if (name === "a" && !closing && /\bclass="button"/.test(attributes)) { flush(); kind = "li"; prefix = "- "; button = true; }
    buffer += inline(name, closing, attributes);
    if (name === "a" && closing && button) { flush(); button = false; }
  }
  flush();
  return blocks.map(([kind, value], index) => (index === 0 ? "" : kind === "li" && blocks[index - 1][0] === "li" ? "\n" : "\n\n") + value).join("") + "\n";
}

// /llms-full.txt: everything the English pages say, for AI assistants that read a site in one request.
function llmsFull(read) {
  const english = pageNames.map(page => `Source: ${url(fallback, page)}\n\n${markdown(read(fallback, page), url(fallback, page))}`);
  return `# ClipDock — full text of the English website

> The home, help and privacy pages of ${origin}/en/ as Markdown, generated from the pages themselves. ClipDock ${version} is a free Windows and Mac app that saves video as MP4 and audio as MP3 from a URL. A short summary with links is in ${origin}/llms.txt. The same pages exist in Japanese (${origin}/), Simplified Chinese (${origin}/zh-cn/) and Spanish (${origin}/es/).

---

${english.join("\n---\n\n")}`;
}

// Each page's last-modified date and a fingerprint of everything it shows, by URL. A page's date (its sitemap lastmod
// and dateModified) moves only when the fingerprint does, whether the body, the head or the generator changed.
const datesFile = fileURLToPath(new URL("site-dates.json", import.meta.url));
function storedDates() {
  try { return JSON.parse(readFileSync(datesFile, "utf8")); } catch { return {}; }
}
const fingerprint = html => createHash("sha256").update(html.replaceAll("\r\n", "\n")).digest("hex");

// Every generated file as it should be: { path: contents }. A page whose fingerprint changed gets `today`; without
// `today` (the check), every page keeps its date, and a page changed since the last run makes site-dates.json stale.
export function render(today) {
  const stored = storedDates();
  const files = {};
  const dates = {};
  const record = {};
  const sources = new Map();
  for (const language of languages) for (const page of pageNames) {
    const file = pageFile(language, page);
    const html = readFileSync(file, "utf8");
    const loc = url(language, page);
    const content = fingerprint(withHead(language, page, html, "0000-00-00"));
    let date = stored[loc]?.date;
    if (today && (!date || stored[loc].content !== content)) date = today;
    date ??= "missing";
    files[file] = withHead(language, page, html, date);
    dates[loc] = date;
    record[loc] = { date, content };
    sources.set(file, html);
  }
  files[datesFile] = JSON.stringify(record, null, 2) + "\n";
  files[join(root, "sitemap.xml")] = sitemap(dates);
  files[join(root, "llms.txt")] = llms();
  files[join(root, "llms-full.txt")] = llmsFull((language, page) => sources.get(pageFile(language, page)));
  return files;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const check = process.argv.includes("--check");
  const now = new Date();
  const today = new Date(now.getTime() - now.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
  const files = render(check ? undefined : today);
  const current = file => { try { return readFileSync(file, "utf8"); } catch { return null; } };
  const stale = Object.entries(files).filter(([file, contents]) => !same(current(file), contents)).map(([file]) => file);
  if (check) {
    if (stale.length) { console.error(`Out of date; run node scripts/site-metadata.mjs:\n${stale.join("\n")}`); process.exit(1); }
    console.log("Website metadata is up to date.");
  } else {
    for (const [file, contents] of Object.entries(files)) writeFileSync(file, contents);
    console.log(`Wrote ${Object.keys(files).length} files (${stale.length} changed).`);
  }
}
