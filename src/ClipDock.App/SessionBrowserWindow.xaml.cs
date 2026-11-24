using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ClipDock.Core;
using Localization = ClipDock.Core.Localization;
using Microsoft.Web.WebView2.Core;

namespace ClipDock.App;

public partial class SessionBrowserWindow : Window
{
    private readonly BrowserSession session;
    private readonly Func<bool> hasActiveDownloads;
    private readonly Action<string> receiveUrl;
    private Task? initialization;
    private string pendingUrl = "";
    private bool closed;

    internal SessionBrowserWindow(BrowserSession session, Func<bool> hasActiveDownloads, Action<string> receiveUrl)
    {
        InitializeComponent();
        FontFamily = UiFont.For(ClipDock.Core.Localization.Language);
        this.session = session; this.hasActiveDownloads = hasActiveDownloads; this.receiveUrl = receiveUrl;
        Closed += (_, _) => { closed = true; Web.Dispose(); };
    }
    internal async void Open(string url)
    {
        pendingUrl = url;
        try
        {
            await (initialization ??= InitializeAsync());
            if (!closed && pendingUrl.Length > 0) Web.CoreWebView2.Navigate(pendingUrl);
        }
        catch (Exception ex) { if (!closed) { initialization = null; Status.Text = ex.Message; RuntimeHelp.Visibility = Visibility.Visible; } }
    }
    private async Task InitializeAsync()
    {
        await Web.EnsureCoreWebView2Async(await session.EnvironmentAsync());
        if (closed) return;
        var core = Web.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Auto;
        core.NavigationStarting += (_, e) =>
        {
            if (!SiteSessionScope.AllowsNavigation(e.Uri)) { e.Cancel = true; Status.Text = Localization.Text("ログインにはhttpsのページを開いてください。"); }
            else Address.Text = e.Uri;
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; if (SiteSessionScope.AllowsNavigation(e.Uri)) core.Navigate(e.Uri); };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => { e.Cancel = true; Status.Text = Localization.Text("動画は「このページを取り込む」から保存してください。"); };
        core.NavigationCompleted += async (_, e) =>
        {
            if (closed) return;
            try { Address.Text = core.Source; Status.Text = e.IsSuccess ? await session.RefreshStatusAsync(core.Source) : Localization.Text("ページを開けませんでした。URLを確認して、もう一度開いてください。"); }
            catch (Exception) { if (!closed) Status.Text = Localization.Text("ログイン状態を確認できませんでした。サイトを開き直してください。"); }
        };
    }
    private void Go_Click(object sender, RoutedEventArgs e) => OpenAddress();
    private void Address_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OpenAddress(); e.Handled = true; } }
    private void OpenAddress()
    {
        var url = Address.Text.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!SiteSessionScope.AllowsNavigation(url)) { Status.Text = Localization.Text("httpsのページURLを入力してください。"); return; }
        Open(url);
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoBack == true) Web.CoreWebView2.GoBack(); }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (hasActiveDownloads()) { Status.Text = Localization.Text("保存が終わるか、キャンセルしてからログイン情報を削除してください。"); return; }
        var confirm = new ConfirmDialog(this, Localization.Text("ログイン情報を削除"),
            Localization.Text("ClipDock内のすべてのログイン情報と閲覧データを削除しますか？"), Localization.Text("削除する"), Localization.Text("キャンセル"));
        if (confirm.ShowDialog() != true) return;
        try { await session.ClearAsync(); if (!closed) { Status.Text = Localization.Text("ログイン情報を削除しました。"); Web.CoreWebView2?.Reload(); } }
        catch (Exception) { if (!closed) Status.Text = Localization.Text("削除できませんでした。画面を開き直して再実行してください。"); }
    }
    private void UsePage_Click(object sender, RoutedEventArgs e)
    {
        var url = Web.CoreWebView2?.Source ?? "";
        try { new DownloadRequest { Url = url }.ValidateVideoPage(); }
        catch (ArgumentException ex) { Status.Text = ex.Message; return; }
        receiveUrl(url); Close();
    }
    private void Runtime_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { Status.Text = Localization.Text("Microsoft公式のWebView2ダウンロードページをブラウザーで開いてください。"); }
    }
}
