using System.IO;
using System.Windows;
using System.Windows.Interop;
using ClipDock.Core;
using Microsoft.Web.WebView2.Core;

namespace ClipDock.App;

internal sealed class BrowserSession(Window owner) : IDisposable
{
    private readonly string profileDirectory = Path.Combine(AppPaths.Data, "browser-profile");
    private Task<CoreWebView2Environment>? environment;
    private Task<CoreWebView2Controller>? controller;
    private SessionBrowserWindow? browser;
    private bool disposed;
    private string? currentUrl;
    public event Action<string>? StatusChanged;

    internal Task<CoreWebView2Environment> EnvironmentAsync() => environment ??= CreateEnvironmentAsync();
    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        try { return await CoreWebView2Environment.CreateAsync(null, profileDirectory); }
        catch { environment = null; throw new InvalidOperationException("ログイン画面にはMicrosoft Edge WebView2 Runtimeが必要です。Microsoft公式サイトからインストールしてください。"); }
    }
    private async Task<CoreWebView2Controller> CreateControllerAsync()
    {
        try
        {
            var env = await EnvironmentAsync();
            var result = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(owner).EnsureHandle());
            result.IsVisible = false;
            if (disposed) { result.Close(); throw new ObjectDisposedException(nameof(BrowserSession)); }
            return result;
        }
        catch { controller = null; throw; }
    }
    private async Task<CoreWebView2> CoreAsync() => (await (controller ??= CreateControllerAsync())).CoreWebView2;
    private async Task<List<SessionCookie>> CookiesAsync(string url)
    {
        var result = new List<SessionCookie>();
        var core = await CoreAsync();
        foreach (var origin in SiteSessionScope.CookieOrigins(url))
        {
            var cookies = await core.CookieManager.GetCookiesAsync(origin);
            result.AddRange(cookies.Select(c => new SessionCookie(c.Domain, c.Path, c.Name, c.Value, c.IsSecure, c.IsHttpOnly,
                c.IsSession ? 0 : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds())));
        }
        return result;
    }
    internal async Task<SessionCookieFile?> CreateCookieFileAsync(DownloadRequest request, CancellationToken ct)
    {
        if (!request.UseLogin || SiteSessionScope.CookieOrigins(request.Url).Length == 0 || !Directory.Exists(profileDirectory)) return null;
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
        var status = currentUrl is null || !Directory.Exists(profileDirectory) ? "ログインが必要なサイトは「ログイン」から接続できます。" :
            $"{new Uri(currentUrl).Host}: {(HasLogin(currentUrl, await CookiesAsync(currentUrl)) ? SiteSessionScope.Site(currentUrl) is null ? "サイト情報を利用できます" : "ログイン情報あり" : "未接続")}";
        StatusChanged?.Invoke(status);
        return status;
    }
    internal void Show(string? url, Func<bool> hasActiveDownloads, Action<string> receiveUrl)
    {
        if (browser is null)
        {
            browser = new SessionBrowserWindow(this, hasActiveDownloads, receiveUrl) { Owner = owner };
            browser.Closed += (_, _) => browser = null;
        }
        browser.Show(); browser.Activate();
        browser.Open(SiteSessionScope.AllowsNavigation(url ?? "") ? url! : "");
    }
    internal async Task ClearAsync()
    {
        var core = await CoreAsync();
        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        await RefreshStatusAsync();
    }
    public void Dispose()
    {
        disposed = true;
        browser?.Close();
        if (controller?.IsCompletedSuccessfully == true) controller.Result.Close();
    }
}
