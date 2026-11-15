using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClipDock.Core;

namespace ClipDock.Mac;

public partial class LoginWindow : Window
{
    private readonly BrowserSession session;
    private readonly Func<bool> hasActiveDownloads;
    private readonly Action<string> receiveUrl;
    private readonly TaskCompletionSource adapter = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string pageUrl = "";
    private bool closed;

    // Raised with the page address and whether the page loaded; used by the automated window check.
    internal event Action<string, bool>? PageLoaded;

    internal LoginWindow(BrowserSession session, Func<bool> hasActiveDownloads, Action<string> receiveUrl)
    {
        InitializeComponent();
        this.session = session; this.hasActiveDownloads = hasActiveDownloads; this.receiveUrl = receiveUrl;
        BrowserSession.Configure(Web);
        Web.AdapterCreated += (_, _) => adapter.TrySetResult();
        Web.NavigationStarted += (_, e) =>
        {
            var url = e.Request?.ToString() ?? "";
            if (url.Length == 0 || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            if (!SiteSessionScope.AllowsNavigation(url)) { e.Cancel = true; Status.Text = ClipDock.Core.Localization.Text("ログインにはhttpsのページを開いてください。"); }
            else Address.Text = url;
        };
        // Pop-ups and target=_blank links stay in this window; nothing opens outside the app's session.
        Web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Request is { } target && SiteSessionScope.AllowsNavigation(target.ToString())) Web.Navigate(target);
        };
        Web.NavigationCompleted += async (_, e) =>
        {
            if (closed) return;
            var url = e.Request?.ToString() ?? "";
            if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (SiteSessionScope.AllowsNavigation(url)) { pageUrl = url; Address.Text = url; }
                Status.Text = e.IsSuccess ? await session.RefreshStatusAsync(pageUrl.Length > 0 ? pageUrl : null) : ClipDock.Core.Localization.Text("ページを開けませんでした。URLを確認して、もう一度開いてください。");
            }
            catch (Exception) { if (!closed) Status.Text = ClipDock.Core.Localization.Text("ログイン状態を確認できませんでした。サイトを開き直してください。"); }
            PageLoaded?.Invoke(url, e.IsSuccess);
        };
        Closed += (_, _) => closed = true;
    }

    internal async void Open(string url)
    {
        if (url.Length == 0) return;
        try
        {
            await adapter.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (!closed) Web.Navigate(new Uri(url));
        }
        catch (Exception) { if (!closed) Status.Text = ClipDock.Core.Localization.Text("ログイン画面を準備できませんでした。画面を開き直してください。"); }
    }

    // Single-page sites change the address without a navigation, so ask the page itself first.
    internal async Task<string> CurrentUrlAsync()
    {
        try
        {
            var value = await Web.InvokeScript("window.location.href").WaitAsync(TimeSpan.FromSeconds(3));
            if (value is { Length: > 1 } && value[0] == '"') value = JsonSerializer.Deserialize<string>(value);
            if (value is not null && SiteSessionScope.AllowsNavigation(value)) return value;
        }
        catch (Exception) { /* Fall back to the last completed navigation. */ }
        return pageUrl;
    }

    private void Go_Click(object? sender, RoutedEventArgs e) => OpenAddress();
    private void Address_KeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OpenAddress(); e.Handled = true; } }
    private void OpenAddress()
    {
        var url = (Address.Text ?? "").Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!SiteSessionScope.AllowsNavigation(url)) { Status.Text = ClipDock.Core.Localization.Text("httpsのページURLを入力してください。"); return; }
        Open(url);
    }
    private void Back_Click(object? sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }
    private async void Clear_Click(object? sender, RoutedEventArgs e)
    {
        if (hasActiveDownloads()) { Status.Text = ClipDock.Core.Localization.Text("保存が終わるか、キャンセルしてからログイン情報を削除してください。"); return; }
        if (!await ConfirmDialog.ShowAsync(this, ClipDock.Core.Localization.Text("ログイン情報を削除"), ClipDock.Core.Localization.Text("ClipDock内のすべてのサイトのログイン情報（Cookie）を削除しますか？"), ClipDock.Core.Localization.Text("削除する"), ClipDock.Core.Localization.Text("キャンセル"))) return;
        try { await session.ClearAsync(); if (!closed) { Status.Text = ClipDock.Core.Localization.Text("ログイン情報を削除しました。"); Web.Refresh(); } }
        catch (Exception) { if (!closed) Status.Text = ClipDock.Core.Localization.Text("削除できませんでした。画面を開き直して再実行してください。"); }
    }
    private async void UsePage_Click(object? sender, RoutedEventArgs e)
    {
        var url = await CurrentUrlAsync();
        if (closed) return;
        try { new DownloadRequest { Url = url }.ValidateVideoPage(); }
        catch (ArgumentException ex) { Status.Text = ex.Message; return; }
        receiveUrl(url); Close();
    }
}
