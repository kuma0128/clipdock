using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClipDock.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record BrowserCookie
{
    public string Domain { get; init; } = "";
    public string Path { get; init; } = "/";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public bool Secure { get; init; }
    public bool HttpOnly { get; init; }
    public bool HostOnly { get; init; }
    public double? ExpirationDate { get; init; }
}

public sealed record DownloadRequest
{
    public string Action { get; init; } = "download";
    public string Url { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string Referer { get; init; } = "";
    public string Title { get; init; } = "";
    public string Quality { get; init; } = "1080";
    public string UserAgent { get; init; } = "";
    public List<BrowserCookie> Cookies { get; init; } = [];

    public DownloadRequest Validated()
    {
        if (Action is not ("download" or "open" or "ping")) throw new ArgumentException("不明な操作です。");
        if (Action != "download") return new DownloadRequest { Action = Action };
        if (!IsWebUrl(Url)) throw new ArgumentException("http または https の動画・ページURLを指定してください。");
        if (Quality is not ("best" or "2160" or "1080" or "720" or "480")) throw new ArgumentException("画質の指定が正しくありません。");
        if (Cookies is null || Cookies.Count > 500) throw new ArgumentException("Cookieの数が多すぎます。");
        var hosts = new[] { Url, PageUrl, Referer }.Where(IsWebUrl).Select(u => new Uri(u).Host).ToArray();
        var cookies = Cookies.Where(c => c is not null && c.Domain is not null && c.Path is not null &&
            c.Name is not null && c.Value is not null &&
            hosts.Any(h => DomainMatches(h, c.Domain, c.HostOnly)) &&
            new[] { c.Domain, c.Path, c.Name, c.Value }.All(v => v.Length <= 16384 && !v.Any(char.IsControl)) &&
            c.Path.StartsWith('/') && c.Name.Length > 0 &&
            (c.ExpirationDate is null || double.IsFinite(c.ExpirationDate.Value) && c.ExpirationDate >= 0 && c.ExpirationDate < 253402300799)).ToList();
        return this with
        {
            PageUrl = IsWebUrl(PageUrl) ? PageUrl : Url,
            Referer = IsWebUrl(Referer) ? Referer : IsWebUrl(PageUrl) ? PageUrl : "",
            Title = CleanText(Title, 200), UserAgent = CleanText(UserAgent, 512), Cookies = cookies
        };
    }

    public static string CleanText(string? text, int limit)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c)).Take(limit).ToArray());
        // The limit can fall inside a surrogate pair (emoji and the like); do not keep half of it.
        return clean.Length > 0 && char.IsHighSurrogate(clean[^1]) ? clean[..^1] : clean;
    }
    public static bool IsWebUrl(string? value) => value is { Length: > 0 and <= 16384 } && !value.Any(char.IsControl) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
    public static bool DomainMatches(string host, string domain, bool hostOnly)
    {
        var bare = domain.TrimStart('.');
        if (bare.Length == 0 || !bare.Contains('.')) return string.Equals(host, bare, StringComparison.OrdinalIgnoreCase);
        return string.Equals(host, bare, StringComparison.OrdinalIgnoreCase) ||
               !hostOnly && host.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase);
    }
}

public static class AppPaths
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    public static string Data => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipDock");
    public static string Inbox => Path.Combine(Data, "inbox");
    public static string Tools => Path.Combine(Data, "tools");
    public static string Secrets => Path.Combine(Data, "sessions");
    public static string DefaultOutput => Path.Combine(DownloadsDirectory(), "ClipDock");

    private static string DownloadsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(in DownloadsFolderId, 0, IntPtr.Zero, out path) == 0 &&
                    Marshal.PtrToStringUni(path) is { Length: > 0 } downloads)
                    return downloads;
            }
            finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
