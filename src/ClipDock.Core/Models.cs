using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClipDock.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record DownloadRequest
{
    public string Url { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string Referer { get; init; } = "";
    public string Title { get; init; } = "";
    public string Quality { get; init; } = "1080";
    public string Format { get; init; } = "mp4";
    public string UserAgent { get; init; } = "";
    public bool UseLogin { get; init; } = true;
    public DownloadRequest Validated()
    {
        if (!IsWebUrl(Url)) throw new ArgumentException("http または https の動画・ページURLを指定してください。");
        if (Quality is not ("best" or "2160" or "1080" or "720" or "480")) throw new ArgumentException("画質の指定が正しくありません。");
        if (Format is not ("mp4" or "mp3")) throw new ArgumentException("保存形式の指定が正しくありません。");
        return this with
        {
            PageUrl = IsWebUrl(PageUrl) ? PageUrl : Url,
            Referer = IsWebUrl(Referer) ? Referer : IsWebUrl(PageUrl) ? PageUrl : "",
            Title = CleanText(Title, 200), UserAgent = CleanText(UserAgent, 512)
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

    public void ValidateVideoPage()
    {
        if (!IsWebUrl(Url)) throw new ArgumentException("http または https の動画・ページURLを指定してください。");
        var page = new Uri(Url);
        if (SiteSessionScope.IsYouTube(page.Host) && !IsYouTubeVideo(page))
            throw new ArgumentException("YouTubeの動画ページを開いて、そのURLを入力してください。トップページや検索結果のURLでは保存できません。");
    }

    private static bool IsYouTubeVideo(Uri page)
    {
        var path = page.AbsolutePath.Trim('/');
        if (SiteSessionScope.IsDomain(page.Host, "youtu.be")) return path.Length > 0 && !path.Contains('/');
        if (path == "watch") return page.Query.TrimStart('?').Split('&').Any(p => p.StartsWith("v=", StringComparison.Ordinal) && p.Length > 2);
        return new[] { "shorts/", "embed/", "live/", "clip/", "v/" }.Any(p => path.StartsWith(p, StringComparison.Ordinal) && path.Length > p.Length);
    }
}

public static class AppPaths
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private static string? dataOverride;

    // Windows: %LOCALAPPDATA%\ClipDock. macOS: ~/Library/Application Support/ClipDock.
    public static string Data => dataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipDock");
    public static string Tools => Path.Combine(Data, "tools");

    // Development and automated checks only: keeps a test run away from the user's real settings and history.
    public static void UseDataDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The data directory must be an absolute path.");
        dataOverride = Path.GetFullPath(directory);
    }

    // Engine executables carry the platform's usual file name: "yt-dlp.exe" on Windows, "yt-dlp" elsewhere.
    public static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
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
