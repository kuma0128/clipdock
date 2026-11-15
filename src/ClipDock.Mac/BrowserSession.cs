using System.Net;
using Avalonia.Controls;
using Avalonia.Platform;
using ClipDock.Core;

namespace ClipDock.Mac;

// The app's own sign-in session: a WKWebView data store (WebView2 profile on Windows) that is separate
// from Safari, Chrome and Edge. Cookies are read from it only when a download starts.
internal sealed class BrowserSession
{
    private readonly Window owner;
    private readonly NativeWebView store;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LoginWindow? browser;
    private string? currentUrl;
    public event Action<string>? StatusChanged;

    // store: a web view that stays in the main window so cookies can be read while the sign-in window is closed.
    internal BrowserSession(Window owner, NativeWebView store)
    {
        this.owner = owner;
        this.store = store;
        Configure(store);
        store.AdapterCreated += (_, _) => ready.TrySetResult();
    }

    // Every web view of the app must use the same environment, otherwise it would see a different session.
    internal static void Configure(NativeWebView view) => view.EnvironmentRequested += (_, e) =>
    {
        // Sites commonly refuse or degrade for an unknown browser; WKWebView omits this part of Safari's own identification.
        if (e is AppleWKWebViewEnvironmentRequestedEventArgs apple) apple.ApplicationNameForUserAgent = "Version/18.0 Safari/605.1.15";
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows) windows.UserDataFolder = Path.Combine(AppPaths.Data, "browser-profile");
    };

    internal Task WhenReadyAsync() => ready.Task;

    private async Task<List<SessionCookie>> CookiesAsync(string url)
    {
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { throw new InvalidOperationException(ClipDock.Core.Localization.Text("ログイン情報を読み込めませんでした。アプリを開き直して再試行してください。")); }
        var manager = store.TryGetCookieManager() ?? throw new InvalidOperationException(ClipDock.Core.Localization.Text("ログイン情報を読み込めませんでした。アプリを開き直して再試行してください。"));
        return (await manager.GetCookiesAsync()).Where(c => SiteSessionScope.AllowsCookie(url, c.Domain)).Select(Convert).ToList();
    }

    private static SessionCookie Convert(Cookie cookie) => new(cookie.Domain, cookie.Path, cookie.Name, cookie.Value, cookie.Secure, cookie.HttpOnly,
        cookie.Expires == default ? 0 : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds());

    internal async Task<SessionCookieFile?> CreateCookieFileAsync(DownloadRequest request, CancellationToken ct)
    {
        if (!request.UseLogin || SiteSessionScope.CookieOrigins(request.Url).Length == 0) return null;
        ct.ThrowIfCancellationRequested();
        var cookies = await CookiesAsync(request.Url);
        ct.ThrowIfCancellationRequested();
        return HasLogin(request.Url, cookies) ? SessionCookieFile.Create(request.Url, cookies, Path.Combine(AppPaths.Data, "sessions")) : null;
    }

    private static bool HasLogin(string url, IEnumerable<SessionCookie> cookies) => cookies.Any(c =>
        c.Value.Length > 0 && (c.Expires == 0 || c.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) &&
        (SiteSessionScope.Site(url) switch { "x" => c.Name == "auth_token", "youtube" => c.Name is "SAPISID" or "__Secure-3PAPISID" or "__Secure-1PAPISID", _ => true }));

    internal async Task<string> RefreshStatusAsync(string? url = null)
    {
        if (url is not null && SiteSessionScope.AllowsNavigation(url)) currentUrl = url;
        var status = currentUrl is null ? ClipDock.Core.Localization.Text("ログインが必要なサイトは「ログイン」から接続できます。") :
            $"{new Uri(currentUrl).Host}: {(HasLogin(currentUrl, await CookiesAsync(currentUrl)) ? SiteSessionScope.Site(currentUrl) is null ? ClipDock.Core.Localization.Text("サイト情報を利用できます") : ClipDock.Core.Localization.Text("ログイン情報あり") : ClipDock.Core.Localization.Text("未接続"))}";
        StatusChanged?.Invoke(status);
        return status;
    }

    internal LoginWindow Show(string? url, Func<bool> hasActiveDownloads, Action<string> receiveUrl)
    {
        if (browser is null)
        {
            browser = new LoginWindow(this, hasActiveDownloads, receiveUrl);
            browser.Closed += (_, _) => browser = null;
            browser.Show(owner);
        }
        browser.Activate();
        browser.Open(SiteSessionScope.AllowsNavigation(url ?? "") ? url! : "");
        return browser;
    }

    // Removes every cookie of the app's session, for all sites.
    internal async Task ClearAsync()
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var manager = store.TryGetCookieManager() ?? throw new InvalidOperationException("Cookie store unavailable.");
        foreach (var cookie in await manager.GetCookiesAsync()) manager.DeleteCookie(cookie);
        await RefreshStatusAsync();
    }

    internal void Close() => browser?.Close();
}
