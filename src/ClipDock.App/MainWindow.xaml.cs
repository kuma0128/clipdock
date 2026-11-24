using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipDock.Core;
using Localization = ClipDock.Core.Localization;
using Microsoft.Win32;

namespace ClipDock.App;

public partial class MainWindow : Window
{
    private readonly DownloadLibrary library = new();
    private readonly DispatcherTimer activationTimer;
    private readonly string historyFile = Path.Combine(AppPaths.Data, "history.dat");
    private readonly string settingsFile = Path.Combine(AppPaths.Data, "settings.json");
    private string outputDirectory = AppPaths.DefaultOutput;
    private Task? installTask;
    private CancellationTokenSource? installCancellation;
    private bool installing;
    // The engine download has been tried at least once since launch; until then a missing engine is about to be fetched.
    private bool engineTried;
    // The launch-time update of an engine that already works: it reports nothing and never asks before closing.
    private bool updatingQuietly;
    private bool shuttingDown;
    private bool allowClose;
    private readonly BrowserOverlay browserOverlay;
    private readonly BrowserSession browserSession;
    // Closing the window keeps ClipDock in the notification area; only the icon's menu quits it.
    private readonly TrayIcon trayIcon;
    private readonly StartupRegistration startup = new();
    private bool startAtLogin = true;
    private bool trayNoticeShown;
    private bool exitRequested;
    private bool sessionEnding;
    private bool changingStartup;
    // Launch checks the engine for updates. ClipDock can then wait in the notification area for days,
    // so it checks again once a day while nothing is being saved, and retries sooner after a failure.
    private static readonly TimeSpan EngineRecheck = TimeSpan.FromHours(24);
    private readonly DispatcherTimer engineTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private DateTime nextEngineCheck = DateTime.MaxValue;
    // Self-update: checked at launch and once a day, downloaded in the background and verified against the
    // official site's manifest, then installed while ClipDock waits in the notification area with nothing to save.
    private readonly string updateDirectory = Path.Combine(AppPaths.Data, "updates");
    private readonly string updateMarker = Path.Combine(AppPaths.Data, "update-installing.txt");
    private readonly CancellationTokenSource updateCancellation = new();
    private DateTime nextUpdateCheck = DateTime.MaxValue;
    private bool checkingUpdate;
    private bool updateRequested;
    private bool applyingUpdate;
    private UpdateOffer? updateOffer;
    private string? updateFile;

    public MainWindow(EventWaitHandle activation)
    {
        // Until a language is chosen in the app, Windows' display language decides.
        Localization.Initialize(settingsFile, [System.Globalization.CultureInfo.CurrentUICulture.Name]);
        foreach (var resource in Localization.Resources) System.Windows.Application.Current.Resources[resource.Key] = resource.Value;
        InitializeComponent();
        FontFamily = UiFont.For(Localization.Language);
        trayIcon = new TrayIcon(ShowFromTray, RequestExit);
        VersionLabel.Text = $"ClipDock  {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}  /  Windows";
        Directory.CreateDirectory(AppPaths.Data);
        var oldSessions = Path.Combine(AppPaths.Data, "sessions");
        if (Directory.Exists(oldSessions))
            foreach (var file in Directory.EnumerateFiles(oldSessions, "*.cookies"))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        JobList.ItemsSource = library.Jobs;
        library.StateChanged += RefreshSummary;
        library.Settled += _ => DownloadSettled();
        LoadState();
        ApplyStartupAtLaunch();
        ReportFinishedUpdate();
        RemoveInstalledUpdates();
        foreach (ComboBoxItem language in LanguageBox.Items)
            if ((string?)language.Tag == Localization.Language) LanguageBox.SelectedItem = language;
        LanguageBox.SelectionChanged += Language_Changed;
        browserSession = new BrowserSession(this);
        browserSession.StatusChanged += message => LoginStatus.Text = message;
        browserOverlay = new BrowserOverlay(ReceiveBrowserAddress);
        browserOverlay.Enabled = BrowserOverlayBox.IsChecked == true;
        OutputLabel.Text = outputDirectory;
        RefreshSummary();
        UpdateEngineStatus();
        activationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        activationTimer.Tick += (_, _) =>
        {
            if (shuttingDown || !activation.WaitOne(0)) return;
            ShowFromTray();
        };
        activationTimer.Start();
        engineTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= nextUpdateCheck) _ = CheckForUpdateAsync();
            RemoveInstalledUpdates();
            TryApplyUpdate(updateRequested);
            if (DateTime.UtcNow < nextEngineCheck || installing || shuttingDown || library.ActiveOperations > 0) return;
            installTask = InstallEngineAsync();
        };
        engineTimer.Start();
        StartupBox.Checked += Startup_Changed;
        StartupBox.Unchecked += Startup_Changed;
        System.Windows.Application.Current.SessionEnding += (_, _) =>
        {
            // Windows is signing out or shutting down: never hold it up with a question or a hidden window.
            sessionEnding = true;
            SaveState();
        };
        QualityBox.SelectionChanged += (_, _) => SaveState();
        Mp4Option.Checked += (_, _) => { UpdateFormat(); SaveState(); };
        Mp3Option.Checked += (_, _) => { UpdateFormat(); SaveState(); };
        BrowserOverlayBox.Checked += BrowserOverlay_Changed;
        BrowserOverlayBox.Unchecked += BrowserOverlay_Changed;
        UseLoginBox.Checked += (_, _) => SaveState();
        UseLoginBox.Unchecked += (_, _) => SaveState();
        UrlInput.TextChanged += (_, _) => ShowUrlMessage(null);
        // Not tied to Loaded: at sign-in the window stays hidden, and the engine is still checked at launch.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            installTask = InstallEngineAsync();
            _ = CheckForUpdateAsync();
            try { await browserSession.RefreshStatusAsync(); }
            catch (Exception ex) { LoginStatus.Text = ex.Message; }
        }, DispatcherPriority.Background);
        UpdateFormat();
        Closing += OnClosing;
        Closed += (_, _) => { engineTimer.Stop(); updateCancellation.Cancel(); trayIcon.Dispose(); browserOverlay.Dispose(); browserSession.Dispose(); };
    }

    private void ShowFromTray()
    {
        if (shuttingDown) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void HideToTray()
    {
        Hide();
        // A downloaded update is installed as soon as the window goes away and nothing is being saved.
        if (TryApplyUpdate(updateRequested) || trayNoticeShown) return;
        // Said once, the first time the window disappears into the notification area.
        trayNoticeShown = true;
        trayIcon.Notify(Localization.Text("ClipDockは通知領域で動作しています"),
            Localization.Text("アイコンをクリックすると開きます。終了するには、アイコンを右クリックして「ClipDockを終了」を選びます。"));
        SaveState();
    }
    private void RequestExit()
    {
        if (shuttingDown) return;
        exitRequested = true;
        // A question about unfinished work is asked over the window, so bring it back first.
        if (installing && !updatingQuietly || library.HasUnfinished) ShowFromTray();
        // A window that was never shown needs its handle for Closing to run.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        Close();
    }

    private void ApplyStartupAtLaunch()
    {
        changingStartup = true;
        try
        {
            if (Environment.ProcessPath is not { } executable || AppPaths.UsesCustomDataDirectory) { StartupBox.IsChecked = startAtLogin; return; }
            // Keeps the entry pointing at this copy; a switch turned off in Windows' Startup apps stays off.
            if (startAtLogin) startup.Enable(executable, clearWindowsSwitch: false);
            StartupBox.IsChecked = startAtLogin && startup.IsEnabled(executable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { StartupBox.IsChecked = startAtLogin; }
        finally { changingStartup = false; }
    }
    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (changingStartup || shuttingDown) return;
        var enable = StartupBox.IsChecked == true;
        try
        {
            if (Environment.ProcessPath is { } executable && !AppPaths.UsesCustomDataDirectory)
            {
                if (enable) startup.Enable(executable, clearWindowsSwitch: true);
                else startup.Disable();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            changingStartup = true;
            StartupBox.IsChecked = !enable;
            changingStartup = false;
            ShowAlert(Localization.Text("Windowsの起動設定を変更できませんでした: ") + ex.Message);
            return;
        }
        startAtLogin = enable;
        SaveState();
    }

    // Each setting explains itself where it is chosen; a change says nothing more.
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (shuttingDown || LanguageBox.SelectedItem is not ComboBoxItem) return;
        SaveState();
    }
    private string SelectedLanguage => Localization.Normalize((LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string);

    private string Quality => (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1080";
    private string Format => Mp3Option.IsChecked == true ? "mp3" : "mp4";
    private void UpdateFormat() => QualityBox.IsEnabled = Format == "mp4";
    // The page from the sign-in window lands in the URL field, ready to be checked and saved.
    private void Login_Click(object sender, RoutedEventArgs e) => browserSession.Show(UrlInput.Text.Trim() is { Length: > 0 } url ? url : library.Jobs.FirstOrDefault()?.Request.Url,
        () => library.HasUnfinished, url => { UrlInput.Text = url; Activate(); UrlInput.Focus(); });
    private void BrowserOverlay_Changed(object sender, RoutedEventArgs e)
    {
        browserOverlay.Enabled = BrowserOverlayBox.IsChecked == true;
        SaveState();
    }
    private void ReceiveBrowserAddress(BrowserAddressResult result)
    {
        if (shuttingDown) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
        if (result.Url is { } url)
        {
            UrlInput.Text = url;
            // A completed scheme is the one guess in the address; everything else is as the browser showed it.
            if (result.AddedScheme) ShowUrlMessage(ClipDock.Core.Localization.Text("ブラウザーの表示に https:// を補いました。URLを確認して「保存」を押してください。"), error: false);
        }
        else ShowUrlMessage(Localization.Text(result.Message));
        UrlInput.Focus();
        UrlInput.SelectAll();
    }
    // Returns false, with the reason under the URL, when the URL is already being saved.
    private bool Add(DownloadRequest source)
    {
        if (shuttingDown) throw new InvalidOperationException(ClipDock.Core.Localization.Text("終了処理中です。アプリを開き直してから保存してください。"));
        if (library.Add(source, outputDirectory) is null)
        {
            ShowUrlMessage(ClipDock.Core.Localization.Text("この動画はすでにダウンロード一覧にあります。"));
            return false;
        }
        SaveState(); RefreshSummary(); StartQueue();
        return true;
    }

    // Problems with the URL show under it until the URL is edited; a note (error: false) is shown in gray.
    private void ShowUrlMessage(string? message, bool error = true)
    {
        UrlMessage.Text = message ?? "";
        UrlMessage.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Muted");
        UrlMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
    // Something the user asked for did not work, or ClipDock could not read or save its data. Stays until closed.
    private void ShowAlert(string message)
    {
        AlertText.Text = message;
        AlertBar.Visibility = Visibility.Visible;
    }
    private void CloseAlert_Click(object sender, RoutedEventArgs e) => AlertBar.Visibility = Visibility.Collapsed;

    private void StartQueue()
    {
        var tools = ToolInstaller.FindTools();
        if (shuttingDown || installing || !ToolInstaller.IsReady(tools)) return;
        library.Start((item, ct) => DownloadEngine.RunAsync(item, tools, action => Dispatcher.Invoke(action), ct,
            (request, token) => Dispatcher.InvokeAsync(() => browserSession.CreateCookieFileAsync(request, token)).Task.Unwrap()));
        EngineButton.IsEnabled = library.ActiveOperations == 0;
    }

    private void DownloadSettled()
    {
        EngineButton.IsEnabled = !shuttingDown && !installing && library.ActiveOperations == 0;
        if (!shuttingDown) SaveState();
        RefreshSummary();
        TryApplyUpdate(updateRequested);
    }

    // Only the installed copy updates itself; portable, development and --data-dir runs never do.
    private static bool SelfUpdateAvailable => !AppPaths.UsesCustomDataDirectory && Environment.ProcessPath is { } path &&
        string.Equals(Path.GetFullPath(path), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClipDock", "ClipDock.exe"),
            StringComparison.OrdinalIgnoreCase);

    private async Task CheckForUpdateAsync()
    {
        if (checkingUpdate || shuttingDown || updateFile is not null || !SelfUpdateAvailable) return;
        checkingUpdate = true;
        try
        {
            var offer = await AppUpdate.CheckAsync(updateCancellation.Token);
            if (offer is not null)
            {
                updateFile = await AppUpdate.DownloadAsync(offer, updateDirectory, updateCancellation.Token);
                updateOffer = offer;
                UpdateButton.Content = Localization.Format($"再起動して ClipDock {offer.Version.ToString(3)} に更新");
                UpdateButton.ToolTip = Localization.Text("保存中の動画がなく、ClipDockが通知領域にあるときは自動で更新します。");
                UpdateButton.Visibility = Visibility.Visible;
            }
            nextUpdateCheck = DateTime.UtcNow + TimeSpan.FromHours(24);
        }
        // Offline, a moved site, a bad download or a fault in this check itself: keep the current version and try again
        // later. Nothing may end the checks for good, as a failure while setting up the client did in 1.0.5.
        catch (Exception) { nextUpdateCheck = DateTime.UtcNow + TimeSpan.FromHours(1); }
        finally { checkingUpdate = false; }
        TryApplyUpdate(updateRequested);
    }

    // Runs the verified installer silently and quits; setup waits for this process, then starts ClipDock again.
    private bool TryApplyUpdate(bool userRequested)
    {
        if (updateFile is null || updateOffer is null || applyingUpdate || shuttingDown || installing || checkingUpdate ||
            library.ActiveOperations > 0 || library.HasUnfinished) return false;
        if (!userRequested && IsVisible) return false;
        try
        {
            File.WriteAllText(updateMarker, updateOffer.Version.ToString(3));
            var start = new ProcessStartInfo(updateFile) { UseShellExecute = false, WorkingDirectory = updateDirectory };
            foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/RELAUNCH=" + (IsVisible ? "window" : "background"),
                "/WAITPID=" + Environment.ProcessId, "/LOG=" + Path.Combine(AppPaths.Data, "update-install.log") })
                start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            try { File.Delete(updateMarker); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            updateFile = null; updateOffer = null; updateRequested = false;
            UpdateButton.Visibility = Visibility.Collapsed;
            nextUpdateCheck = DateTime.UtcNow + TimeSpan.FromHours(1);
            ShowAlert(Localization.Text("更新を開始できませんでした: ") + ex.Message);
            return false;
        }
        applyingUpdate = true;
        exitRequested = true;
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        // Queued: this can run inside the window's own Closing event, where Close is not allowed.
        Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
        return true;
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        updateRequested = true;
        if (TryApplyUpdate(true)) return;
        // Saving goes on; the button says when the update will happen instead.
        UpdateButton.Content = Localization.Text("保存が終わると更新します");
        UpdateButton.IsEnabled = false;
    }

    // After setup restarted ClipDock: say once that the update is done, or that it will be tried again.
    // The installer or disk image of the running version (and anything older) is deleted; not while a download is written.
    private void RemoveInstalledUpdates()
    {
        if (checkingUpdate) return;
        try { AppUpdate.RemoveInstalled(updateDirectory, AppUpdate.Current); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void ReportFinishedUpdate()
    {
        if (!File.Exists(updateMarker)) return;
        try
        {
            var text = File.ReadAllText(updateMarker).Trim();
            File.Delete(updateMarker);
            if (!Version.TryParse(text, out var target)) return;
            if (AppUpdate.Current >= AppUpdate.Normalize(target))
                trayIcon.Notify(Localization.Format($"ClipDock {AppUpdate.Current.ToString(3)} に更新しました"), Localization.Text("通知領域のアイコンから開けます。"));
            else ShowAlert(Localization.Text("前回の更新を完了できませんでした。後でもう一度試します。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddUrl();
    private void UrlInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddUrl(); e.Handled = true; } }
    private void AddUrl()
    {
        try { if (Add(new DownloadRequest { Url = UrlInput.Text.Trim(), PageUrl = UrlInput.Text.Trim(), Quality = Quality, Format = Format, UseLogin = UseLoginBox.IsChecked == true })) UrlInput.Clear(); }
        catch (Exception ex) { ShowUrlMessage(ex.Message); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        library.Cancel(item);
        SaveState();
    }
    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (shuttingDown || (sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (!library.Retry(item, UseLoginBox.IsChecked == true, out var refusal))
        {
            if (refusal is not null) ShowAlert(refusal);
            return;
        }
        SaveState(); StartQueue();
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item || !library.Remove(item)) return;
        SaveState(); RefreshSummary();
    }
    private void RefreshSummary()
    {
        CountLabel.Text = library.CompletedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        trayIcon.Text = library.HasUnfinished ? "ClipDock\n" + library.QueueText() : "ClipDock";
        EmptyState.Visibility = library.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Settings slide in over the page; Esc, the close button or a click on the dimmed page closes them.
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsLayer.Visibility = Visibility.Visible;
        CloseSettingsButton.Focus();
    }
    private void CloseSettings()
    {
        if (SettingsLayer.Visibility != Visibility.Visible) return;
        SettingsLayer.Visibility = Visibility.Collapsed;
        SettingsButton.Focus();
    }
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();
    private void SettingsScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseSettings();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // An open language list closes first, as it would anywhere else.
        if (e.Key != Key.Escape || SettingsLayer.Visibility != Visibility.Visible || LanguageBox.IsDropDownOpen) return;
        CloseSettings();
        e.Handled = true;
    }
    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = ClipDock.Core.Localization.Text("動画の保存先"), InitialDirectory = Directory.Exists(outputDirectory) ? outputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (dialog.ShowDialog(this) != true) return;
        outputDirectory = dialog.FolderName; OutputLabel.Text = outputDirectory; SaveState();
    }
    private void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Opened through the shell rather than "explorer.exe <path>": Explorer splits an unquoted path at commas.
            // The trailing separator makes sure a folder is opened, never a same-named file.
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        { ShowAlert(ClipDock.Core.Localization.Text("フォルダーを開けませんでした: ") + ex.Message); }
    }
    private void OpenOutput_Click(object sender, RoutedEventArgs e) => OpenFolder(outputDirectory);
    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        var url = DownloadRequest.IsWebUrl(item.Request.PageUrl) ? item.Request.PageUrl : item.Request.Url;
        if (!DownloadRequest.IsWebUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception ex) { ShowAlert(ClipDock.Core.Localization.Text("ページを開けませんでした: ") + ex.Message); }
    }
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (!DownloadPlan.IsOutputFile(item.FilePath, item.OutputDirectory, item.Request.Format) || !File.Exists(item.FilePath)) { ShowAlert(ClipDock.Core.Localization.Text("ファイルが見つかりません。移動または削除されていないか確認してください。")); return; }
        try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        { ShowAlert(ClipDock.Core.Localization.Text("ファイルを開けませんでした: ") + ex.Message); }
    }
    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (DownloadPlan.IsOutputFile(item.FilePath, item.OutputDirectory, item.Request.Format) && File.Exists(item.FilePath))
        {
            // Highlight the saved file. /select needs this exact quoted form; Windows file names cannot contain quotes.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            try { Process.Start(new ProcessStartInfo(explorer, $"/select,\"{Path.GetFullPath(item.FilePath)}\"") { UseShellExecute = false })?.Dispose(); return; }
            catch (Win32Exception) { }
        }
        OpenFolder(item.OutputDirectory);
    }
    private async void InstallEngine_Click(object sender, RoutedEventArgs e)
    {
        if (library.ActiveOperations > 0 || installing || shuttingDown) return;
        installTask = InstallEngineAsync();
        await installTask;
    }
    // Never throws: closing the window awaits this task to know the download has really stopped.
    // Runs by itself at launch: the first time it fetches the engine, afterwards it updates it quietly.
    private async Task InstallEngineAsync()
    {
        var quiet = updatingQuietly = ToolInstaller.IsReady(ToolInstaller.FindTools());
        installing = true; EngineButton.IsEnabled = false;
        if (!quiet)
        {
            SetupText.Text = ClipDock.Core.Localization.Text("初期セットアップ中です。保存エンジンを取得しています…");
            EngineProblem.Text = ClipDock.Core.Localization.Text("保存エンジンを取得できませんでした。通信環境を確認して、もう一度準備してください。");
        }
        UpdateEngineStatus();
        using var cancellation = installCancellation = new CancellationTokenSource();
        // Downloads wait for this task, so a quiet update gives up once the network stops answering.
        var patience = TimeSpan.FromSeconds(45);
        if (quiet) cancellation.CancelAfter(patience);
        var checkedEngine = false;
        try
        {
            await ToolInstaller.InstallAsync(AppPaths.Tools, new Progress<string>(message =>
            {
                if (installCancellation != cancellation) return;
                if (quiet) { EngineStatus.Text = message; cancellation.CancelAfter(patience); }
                else SetupText.Text = ClipDock.Core.Localization.Text("初期セットアップ中: ") + message;
            }), cancellation.Token);
            checkedEngine = true;
        }
        catch (OperationCanceledException) when (quiet) { }
        catch (OperationCanceledException) { EngineProblem.Text = shuttingDown ? ClipDock.Core.Localization.Text("エンジンの準備を中止しました。") : ClipDock.Core.Localization.Text("エンジンの取得が時間内に終わりませんでした。通信環境を確認して「保存エンジンを準備」を押してください。"); }
        // The installed engine keeps working when an update cannot be fetched.
        catch (Exception) when (quiet) { }
        catch (Exception ex) { EngineProblem.Text = ClipDock.Core.Localization.Text("エンジンの取得に失敗しました: ") + ex.Message; }
        finally
        {
            // At sign-in the network may not be ready yet: a missing engine is retried within minutes, a failed update within half an hour.
            nextEngineCheck = DateTime.UtcNow + (checkedEngine ? EngineRecheck : quiet ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5));
            installCancellation = null; installing = false; updatingQuietly = false; engineTried = true;
            EngineButton.IsEnabled = !shuttingDown; UpdateEngineStatus(); StartQueue();
        }
    }
    private void UpdateEngineStatus()
    {
        var ready = ToolInstaller.IsReady(ToolInstaller.FindTools());
        EngineStatus.Text = ready ? ClipDock.Core.Localization.Text("● エンジンの準備完了") : installing ? ClipDock.Core.Localization.Text("○ 初期セットアップ中…") : ClipDock.Core.Localization.Text("○ 保存エンジンを取得できていません");
        // Without an engine nothing can be saved: the first download shows its progress, a failed one its reason and a
        // button to try again. Updates of a working engine run by themselves and show nothing.
        var settingUp = !ready && (installing || !engineTried);
        SetupBanner.Visibility = settingUp ? Visibility.Visible : Visibility.Collapsed;
        EngineBanner.Visibility = !ready && !settingUp ? Visibility.Visible : Visibility.Collapsed;
    }
    private void LoadState()
    {
        // Settings and history are read separately, and history entry by entry:
        // one unreadable file or record must not discard everything else.
        try
        {
            if (File.Exists(settingsFile) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile), Json.Options)?.Upgraded() is { } settings)
            {
                if (settings.OutputDirectory is { Length: > 0 } directory && Path.IsPathFullyQualified(directory)) outputDirectory = directory;
                foreach (ComboBoxItem quality in QualityBox.Items) if ((string)quality.Tag == settings.Quality) QualityBox.SelectedItem = quality;
                if (settings.Format == "mp3") Mp3Option.IsChecked = true;
                BrowserOverlayBox.IsChecked = settings.BrowserOverlayEnabled;
                UseLoginBox.IsChecked = settings.UseLogin;
                startAtLogin = settings.StartAtLogin;
                trayNoticeShown = settings.TrayNoticeShown;
            }
        }
        catch (Exception) { ShowAlert(ClipDock.Core.Localization.Text("以前の設定を読み込めませんでした。既定の設定で続行します。")); }
        try
        {
            if (!File.Exists(historyFile)) return;
            library.Restore(JsonSerializer.Deserialize<List<SavedDownload?>>(WindowsProtection.Unprotect(File.ReadAllBytes(historyFile)), Json.Options) ?? []);
        }
        catch (Exception) { ShowAlert(ClipDock.Core.Localization.Text("以前の履歴を読み込めませんでした。新しい履歴で続行します。")); }
    }
    private bool SaveState()
    {
        try
        {
            File.WriteAllText(settingsFile + ".tmp", JsonSerializer.Serialize(new AppSettings(outputDirectory, Quality, Format, BrowserOverlayBox.IsChecked == true, UseLoginBox.IsChecked == true, SelectedLanguage, startAtLogin, trayNoticeShown, AppSettings.CurrentVersion), Json.Options));
            File.Move(settingsFile + ".tmp", settingsFile, true);
            File.WriteAllBytes(historyFile + ".tmp", WindowsProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(library.Snapshot(), Json.Options)));
            File.Move(historyFile + ".tmp", historyFile, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { ShowAlert(ClipDock.Core.Localization.Text("履歴を保存できませんでした: ") + ex.Message); return false; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose || sessionEnding) return;
        // The close button hides the window; downloads and the daily engine check go on in the notification area.
        // Without a notification icon (the taskbar never accepted it) closing quits as before.
        var tray = trayIcon.IsVisible;
        var exiting = exitRequested || !tray;
        exitRequested = false;
        if (!exiting && !shuttingDown) { e.Cancel = true; HideToTray(); return; }
        var hasWork = installing && !updatingQuietly || library.HasUnfinished;
        if (!hasWork && !installing && library.ActiveOperations == 0) { activationTimer.Stop(); engineTimer.Stop(); SaveState(); return; }
        e.Cancel = true;
        if (shuttingDown) return;
        // The engine download can take many minutes on a slow line; it is cancellable like any other work.
        var question = installing
            ? Localization.Text(tray ? "エンジンの準備を中止して終了しますか？\n終了しない場合、準備は通知領域で続きます。" : "エンジンの準備を中止して終了しますか？\n続ける場合は、このウィンドウを最小化してください。")
            : Localization.Text(tray ? "ダウンロードを中断して終了しますか？\n終了しない場合、保存は通知領域で続きます。" : "ダウンロードを中断して終了しますか？\n続ける場合は、このウィンドウを最小化してください。");
        if (hasWork && new ConfirmDialog(this, "ClipDock", question, Localization.Text("終了する"), Localization.Text("続ける")).ShowDialog() != true) return;
        shuttingDown = true;
        browserOverlay.Enabled = false;
        activationTimer.Stop();
        engineTimer.Stop();
        installCancellation?.Cancel();
        var stopDownloads = library.StopAsync();
        try
        {
            if (installTask is not null) await installTask;
            await stopDownloads;
        }
        catch (Exception) { /* The window closes either way; the state below is still saved. */ }
        SaveState(); allowClose = true; Close();
    }
}
