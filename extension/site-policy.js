(() => {
  const storeBuild = globalThis.CLIPDOCK_DISTRIBUTION === "store";
  const excludedHosts = ["youtube.com", "youtube-nocookie.com", "youtu.be", "googlevideo.com", "ytimg.com", "youtube.googleapis.com", "youtubei.googleapis.com"];
  const isRestrictedUrl = (value, enforce = storeBuild) => {
    if (!enforce || typeof value !== "string") return false;
    try {
      const url = new URL(value);
      if (url.protocol === "blob:") return isRestrictedUrl(url.pathname, enforce);
      const host = url.hostname.toLowerCase().replace(/\.$/, "");
      return excludedHosts.some(domain => host === domain || host.endsWith("." + domain));
    } catch { return false; }
  };
  globalThis.ClipDockSitePolicy = Object.freeze({ storeBuild, isRestrictedUrl, excludedHosts: Object.freeze(excludedHosts),
    restrictionMessage: "Chrome ウェブストア版はYouTubeの動画保存に対応していません。" });
})();
