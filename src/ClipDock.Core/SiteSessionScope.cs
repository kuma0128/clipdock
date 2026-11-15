namespace ClipDock.Core;

public static class SiteSessionScope
{
    public static bool IsDomain(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    public static bool IsYouTube(string host) => IsDomain(host, "youtube.com") || IsDomain(host, "youtu.be") || IsDomain(host, "youtube-nocookie.com");
    public static string? Site(string url)
    {
        if (!DownloadRequest.IsWebUrl(url)) return null;
        var host = new Uri(url).Host;
        if (IsDomain(host, "x.com") || IsDomain(host, "twitter.com")) return "x";
        return IsYouTube(host) ? "youtube" : null;
    }
    public static string[] CookieOrigins(string url) => Site(url) switch
    {
        "x" => ["https://x.com/", "https://twitter.com/"],
        "youtube" => ["https://www.youtube.com/", "https://youtube.com/"],
        _ => DownloadRequest.IsWebUrl(url) && new Uri(url).Scheme == "https" ? [url] : []
    };
    public static bool AllowsCookie(string url, string domain)
    {
        var host = domain.TrimStart('.');
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || !DownloadRequest.IsWebUrl(url)) return false;
        return Site(url) switch
        {
            "x" => IsDomain(host, "x.com") || IsDomain(host, "twitter.com"),
            "youtube" => IsDomain(host, "youtube.com"),
            _ => new Uri(url).Scheme == "https" && IsDomain(new Uri(url).Host, host)
        };
    }
    public static bool AllowsNavigation(string url)
    {
        if (!DownloadRequest.IsWebUrl(url) || new Uri(url).Scheme != "https") return false;
        return true;
    }
}
