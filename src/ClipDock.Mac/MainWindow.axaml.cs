using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClipDock.App;
using ClipDock.Core;

namespace ClipDock.Mac;

public partial class MainWindow : Window
{
    private readonly DownloadLibrary library = new();
    private readonly string historyFile = Path.Combine(AppPaths.Data, "history.dat");
    private readonly string settingsFile = Path.Combine(AppPaths.Data, "settings.json");
    private string outputDirectory = AppPaths.DefaultOutput;
    private Task? installTask;
    private CancellationTokenSource? installCancellation;
    private bool installing;
    // The launch-time update of an engine that already works: it reports nothing and never asks before closing.
    private bool updatingQuietly;
    private bool shuttingDown;
    private bool allowClose;
    private bool loaded;
    private readonly BrowserSession browserSession;
    // Closing the window keeps ClipDock in the menu bar; only the icon's menu or Quit (⌘Q) ends it.
    private readonly MenuBarIcon menuBarIcon;
    private readonly LoginItem loginItem = new();
    private bool startAtLogin = true;
    private bool trayNoticeShown;
    private bool exitRequested;
    private bool changingLoginItem;
    private bool statusRead;
    // Launch checks the engine for updates. ClipDock can then wait in the menu bar for days,
    // so it checks again once a day while nothing is being saved, and retries sooner after a failure.
    private static readonly TimeSpan EngineRecheck = TimeSpan.FromHours(24);
    private readonly DispatcherTimer engineTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private DateTime nextEngineCheck = DateTime.MaxValue;
    // Self-update: checked at launch and once a day, downloaded in the background and verified against the
    // official site's manifest, then installed while ClipDock waits in the menu bar with nothing to save.
    private readonly string updateDirectory = Path.Combine(AppPaths.Data, "updates");
    private readonly string updateMarker = Path.Combine(AppPaths.Data, "update-installing.txt");
    private readonly CancellationTokenSource updateCancellation = new();
    private DateTime nextUpdateCheck = DateTime.MaxValue;
    private bool checkingUpdate;
    private bool updateRequested;
    private bool applyingUpdate;
    private UpdateOffer? updateOffer;
    private string? updateFile;

    public MainWindow()
    {
        InitializeComponent();
        menuBarIcon = new MenuBarIcon(ShowFromMenuBar, RequestExit);
        VersionLabel.Text = $"ClipDock  {App.Version}  /  {(OperatingSystem.IsMacOS() ? "macOS" : "Avalonia")}";
        HistoryFile.PrepareDataDirectory();
        var oldSessions = Path.Combine(AppPaths.Data, "sessions");
        if (Directory.Exists(oldSessions))
            foreach (var file in Directory.EnumerateFiles(oldSessions, "*.cookies"))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        JobList.ItemsSource = library.Jobs;
        library.StateChanged += RefreshSummary;
        library.Settled += _ => DownloadSettled();
        LoadState();
        ApplyLoginItemAtLaunch();
        ReportFinishedUpdate();
        RemoveInstalledUpdates();
        foreach (var language in LanguageBox.Items.OfType<ComboBoxItem>())
            if ((string?)language.Tag == Localization.Language) LanguageBox.SelectedItem = language;
        LanguageBox.SelectionChanged += Language_Changed;
        loaded = true;
        browserSession = new BrowserSession(this, SessionStore);
        browserSession.StatusChanged += message => LoginStatus.Text = message;
        OutputLabel.Text = outputDirectory;
        ToolTip.SetTip(OutputLabel, outputDirectory);
        RefreshSummary();
        UpdateEngineStatus();
        QualityBox.SelectionChanged += (_, _) => SaveState();
        // Choosing one format clears the other, so one handler sees every change.
        Mp3Option.IsCheckedChanged += (_, _) => { UpdateFormat(); SaveState(); };
        UseLoginBox.IsCheckedChanged += LoginPreference_Changed;
        StartAtLoginBox.IsCheckedChanged += StartAtLogin_Changed;
        // Esc closes the settings before any control sees it.
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        // Not tied to Opened: at login the window stays closed, and the engine is still checked at launch.
        Dispatcher.UIThread.Post(() =>
        {
            if (App.SmokeResultFile is { } result) { _ = RunSmokeAsync(result); return; }
            installTask = InstallEngineAsync();
            _ = CheckForUpdateAsync();
        }, DispatcherPriority.Background);
        // The sign-in session lives in this window, so it is read the first time the window opens.
        Opened += async (_, _) =>
        {
            if (statusRead || App.SmokeResultFile is not null) return;
            statusRead = true;
            try { await browserSession.RefreshStatusAsync(); }
            catch (Exception ex) { LoginStatus.Text = ex.Message; }
        };
        engineTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= nextUpdateCheck) _ = CheckForUpdateAsync();
            RemoveInstalledUpdates();
            TryApplyUpdate(updateRequested);
            if (DateTime.UtcNow < nextEngineCheck || installing || shuttingDown || library.ActiveOperations > 0) return;
            installTask = InstallEngineAsync();
        };
        engineTimer.Start();
        UpdateFormat();
        Closing += OnClosing;
        // The menu bar icon is removed by the lifetime when the process ends.
        Closed += (_, _) => { engineTimer.Stop(); updateCancellation.Cancel(); browserSession.Close(); };
    }

    internal void ShowFromMenuBar()
    {
        if (shuttingDown) return;
        MacApp.ShowInDock(true);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        MacApp.Activate();
    }
    private async void HideToMenuBar()
    {
        if (!trayNoticeShown && App.SmokeResultFile is null)
        {
            // Said once, the first time the window disappears into the menu bar.
            trayNoticeShown = true;
            SaveState();
            await ConfirmDialog.ShowAsync(this, "ClipDock", Localization.Text("ClipDockはメニューバーで動作しています") + "\n" +
                Localization.Text("ウィンドウを閉じても、保存中の動画はそのまま続きます。メニューバーのClipDockアイコンから、ウィンドウを開いたりClipDockを終了したりできます。"),
                Localization.Text("OK"));
            if (shuttingDown || !IsVisible) return;
        }
        // Hiding would also hide the sign-in window and leave it without an owner; it is opened again from 「ログイン」.
        browserSession.Close();
        Hide();
        MacApp.ShowInDock(false);
        // A downloaded update is installed as soon as the window goes away and nothing is being saved.
        TryApplyUpdate(updateRequested);
    }
    // The menu bar icon's Quit, and macOS's own Quit when there is something to ask about.
    private void RequestExit()
    {
        if (shuttingDown) return;
        exitRequested = true;
        // A question about unfinished work is asked over the window, so bring it back first.
        if (installing && !updatingQuietly || library.HasUnfinished) ShowFromMenuBar();
        Close();
    }
    // ⌘Q, Quit in the Dock, or macOS logging out, restarting or shutting down.
    internal void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (allowClose) return;
        var hasWork = installing && !updatingQuietly || library.HasUnfinished;
        // Unfinished work is asked about over the window, as from the menu bar (a log out waits for the answer).
        if (shuttingDown || hasWork)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(RequestExit);
            return;
        }
        // Nothing to lose: macOS may end ClipDock now, without holding up a log out. The installed engine stays
        // usable when a quiet update is cut short.
        shuttingDown = true;
        engineTimer.Stop();
        installCancellation?.Cancel();
        SaveState();
        allowClose = true;
    }

    private void ApplyLoginItemAtLaunch()
    {
        changingLoginItem = true;
        try
        {
            // Development builds (no app bundle) and --data-dir runs never change the login items.
            if (!OperatingSystem.IsMacOS() || LoginItem.BundleExecutable(Environment.ProcessPath) is not { } executable || AppPaths.UsesCustomDataDirectory)
            { StartAtLoginBox.IsChecked = startAtLogin; return; }
            // A copy opened from the disk image registers nothing; the choice applies once ClipDock runs from Applications.
            if (LoginItem.IsTemporary(executable)) { StartAtLoginBox.IsChecked = false; return; }
            // Keeps the agent pointing at this copy, for example after a move. A choice to turn it off stays off.
            if (startAtLogin) loginItem.Enable(executable);
            StartAtLoginBox.IsChecked = startAtLogin && loginItem.Targets(executable);
            MacApp.RelaunchOnLogin(StartAtLoginBox.IsChecked != true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StartAtLoginBox.IsChecked = startAtLogin; }
        finally { changingLoginItem = false; }
    }
    private void StartAtLogin_Changed(object? sender, RoutedEventArgs e)
    {
        if (changingLoginItem || shuttingDown) return;
        var enable = StartAtLoginBox.IsChecked == true;
        void Revert(string message)
        {
            changingLoginItem = true;
            StartAtLoginBox.IsChecked = !enable;
            changingLoginItem = false;
            Notice.Text = message;
        }
        try
        {
            if (OperatingSystem.IsMacOS() && LoginItem.BundleExecutable(Environment.ProcessPath) is { } executable && !AppPaths.UsesCustomDataDirectory)
            {
                if (enable && LoginItem.IsTemporary(executable))
                { Revert(Localization.Text("ログイン時に開くには、ClipDockを「アプリケーション」フォルダーに移してから開いてください。")); return; }
                if (enable) loginItem.Enable(executable);
                else loginItem.Disable();
                MacApp.RelaunchOnLogin(!enable);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Revert(Localization.Text("ログイン時の起動設定を変更できませんでした: ") + ex.Message); return; }
        startAtLogin = enable;
        SaveState();
        Notice.Text = enable ? Localization.Text("Macへのログイン時に、ClipDockをメニューバーで開始します。") : Localization.Text("Macへのログイン時にはClipDockを開始しません。");
    }

    private void Language_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (shuttingDown || LanguageBox.SelectedItem is not ComboBoxItem) return;
        if (SaveState()) Notice.Text = Localization.Text("言語設定を保存しました。アプリを開き直すと適用されます。", SelectedLanguage);
    }
    private string SelectedLanguage => Localization.Normalize((LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string);

    private string Quality => (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1080";
    private string Format => Mp3Option.IsChecked == true ? "mp3" : "mp4";
    private bool UseLogin => UseLoginBox.IsChecked == true;
    private void UpdateFormat() => QualityBox.IsEnabled = Format == "mp4";
    private void LoginPreference_Changed(object? sender, RoutedEventArgs e)
    {
        SaveState();
        Notice.Text = UseLogin ? ClipDock.Core.Localization.Text("接続済みサイトのログイン情報を保存に使います。初回は「ログイン」から接続してください。") : ClipDock.Core.Localization.Text("次の保存・再試行ではログイン情報を使いません。");
    }
    private void Login_Click(object? sender, RoutedEventArgs e) => browserSession.Show((UrlInput.Text ?? "").Trim() is { Length: > 0 } url ? url : library.Jobs.FirstOrDefault()?.Request.Url,
        () => library.HasUnfinished, url => { UrlInput.Text = url; Notice.Text = ClipDock.Core.Localization.Text("URLと保存先を確認して「保存」を押してください。"); Activate(); });

    private void Add(DownloadRequest source)
    {
        if (shuttingDown) throw new InvalidOperationException(ClipDock.Core.Localization.Text("終了処理中です。アプリを開き直してから保存してください。"));
        if (library.Add(source, outputDirectory) is null)
        {
            Notice.Text = ClipDock.Core.Localization.Text("この動画はすでにダウンロード一覧にあります。");
            return;
        }
        Notice.Text = ToolInstaller.IsReady(ToolInstaller.FindTools()) ? ClipDock.Core.Localization.Text("ダウンロード一覧に追加しました。") : ClipDock.Core.Localization.Text("待機リストに追加しました。初期セットアップが終わると開始します。");
        SaveState(); RefreshSummary(); StartQueue();
    }

    private void StartQueue()
    {
        var tools = ToolInstaller.FindTools();
        if (shuttingDown || installing || !ToolInstaller.IsReady(tools)) return;
        library.Start((item, ct) => DownloadEngine.RunAsync(item, tools, action => Dispatcher.UIThread.Invoke(action), ct,
            (request, token) => Dispatcher.UIThread.InvokeAsync(() => browserSession.CreateCookieFileAsync(request, token))));
        EngineButton.IsEnabled = library.ActiveOperations == 0;
    }

    private void DownloadSettled()
    {
        EngineButton.IsEnabled = !shuttingDown && !installing && library.ActiveOperations == 0;
        if (!shuttingDown) SaveState();
        RefreshSummary();
        TryApplyUpdate(updateRequested);
    }

    private async Task CheckForUpdateAsync()
    {
        if (checkingUpdate || shuttingDown || updateFile is not null || MacUpdater.UpdatableBundle(Environment.ProcessPath) is null) return;
        checkingUpdate = true;
        try
        {
            var offer = await AppUpdate.CheckAsync(updateCancellation.Token);
            if (offer is not null)
            {
                updateFile = await AppUpdate.DownloadAsync(offer, updateDirectory, updateCancellation.Token);
                updateOffer = offer;
                UpdateButton.Content = ClipDock.Core.Localization.Format($"再起動して ClipDock {offer.Version.ToString(3)} に更新");
                ToolTip.SetTip(UpdateButton, ClipDock.Core.Localization.Text("保存中の動画がなく、ClipDockがメニューバーにあるときは自動で更新します。"));
                UpdateButton.IsVisible = true;
            }
            nextUpdateCheck = DateTime.UtcNow + TimeSpan.FromHours(24);
        }
        // Offline, a moved site, a bad download or a fault in this check itself: keep the current version and try again
        // later. Nothing may end the checks for good, as a failure while setting up the client did in 1.0.5.
        catch (Exception) { nextUpdateCheck = DateTime.UtcNow + TimeSpan.FromHours(1); }
        finally { checkingUpdate = false; }
        TryApplyUpdate(updateRequested);
    }

    // Hands the verified disk image to the update script and quits; the script reopens ClipDock when it is done.
    private bool TryApplyUpdate(bool userRequested)
    {
        if (updateFile is null || updateOffer is null || applyingUpdate || shuttingDown || installing || checkingUpdate ||
            library.ActiveOperations > 0 || library.HasUnfinished) return false;
        if (!userRequested && IsVisible) return false;
        if (MacUpdater.UpdatableBundle(Environment.ProcessPath) is not { } bundle) return false;
        try
        {
            File.WriteAllText(updateMarker, updateOffer.Version.ToString(3));
            MacUpdater.Start(bundle, updateFile, updateOffer.Version, IsVisible, Path.Combine(AppPaths.Data, "update-install.log"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            try { File.Delete(updateMarker); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            updateFile = null; updateOffer = null; updateRequested = false;
            UpdateButton.IsVisible = false;
            nextUpdateCheck = DateTime.UtcNow + TimeSpan.FromHours(1);
            Notice.Text = ClipDock.Core.Localization.Text("更新を開始できませんでした: ") + ex.Message;
            return false;
        }
        applyingUpdate = true;
        exitRequested = true;
        // Queued: this can run inside the window's own Closing event.
        Dispatcher.UIThread.Post(Close, DispatcherPriority.Background);
        return true;
    }

    private void Update_Click(object? sender, RoutedEventArgs e)
    {
        updateRequested = true;
        if (!TryApplyUpdate(true)) Notice.Text = ClipDock.Core.Localization.Text("保存中の動画が終わると、ClipDockを再起動して更新します。");
    }

    // After the update script reopened ClipDock: say once that the update is done, or that it will be tried again.
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
            Notice.Text = AppUpdate.Current >= AppUpdate.Normalize(target)
                ? ClipDock.Core.Localization.Format($"ClipDock {AppUpdate.Current.ToString(3)} に更新しました")
                : ClipDock.Core.Localization.Text("前回の更新を完了できませんでした。後でもう一度試します。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Add_Click(object? sender, RoutedEventArgs e) => AddUrl();
    private void UrlInput_KeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddUrl(); e.Handled = true; } }
    private void AddUrl()
    {
        var url = (UrlInput.Text ?? "").Trim();
        try { Add(new DownloadRequest { Url = url, PageUrl = url, Quality = Quality, Format = Format, UseLogin = UseLogin }); UrlInput.Text = ""; }
        catch (Exception ex) { Notice.Text = ex.Message; }
    }

    private static DownloadItem? ItemOf(object? sender) => (sender as Control)?.DataContext as DownloadItem;

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        library.Cancel(item);
        SaveState();
    }
    private void Retry_Click(object? sender, RoutedEventArgs e)
    {
        if (shuttingDown || ItemOf(sender) is not { } item) return;
        var retried = library.Retry(item, UseLogin, out var message);
        if (message is not null) Notice.Text = message;
        if (!retried) return;
        SaveState(); RefreshSummary(); StartQueue();
    }
    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || !library.Remove(item)) return;
        SaveState(); RefreshSummary();
    }
    private void RefreshSummary()
    {
        CountLabel.Text = library.CompletedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        menuBarIcon.Text = library.HasUnfinished ? "ClipDock\n" + library.QueueText() : "ClipDock";
        EmptyState.IsVisible = library.Jobs.Count == 0;
    }

    // Settings slide in over the page; Esc, the close button or a click on the dimmed page closes them.
    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        SettingsLayer.IsVisible = true;
        CloseSettingsButton.Focus();
    }
    private void CloseSettings()
    {
        if (!SettingsLayer.IsVisible) return;
        SettingsLayer.IsVisible = false;
        SettingsButton.Focus();
    }
    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => CloseSettings();
    private void SettingsScrim_PointerPressed(object? sender, PointerPressedEventArgs e) => CloseSettings();
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        // An open language list closes first, as it would anywhere else.
        if (e.Key != Key.Escape || !SettingsLayer.IsVisible || LanguageBox.IsDropDownOpen) return;
        CloseSettings();
        e.Handled = true;
    }
    private async void ChooseOutput_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var start = Directory.Exists(outputDirectory) ? outputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = ClipDock.Core.Localization.Text("動画の保存先"), AllowMultiple = false, SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start)
            });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } chosen || !Path.IsPathFullyQualified(chosen)) return;
            outputDirectory = chosen; OutputLabel.Text = outputDirectory; ToolTip.SetTip(OutputLabel, outputDirectory); SaveState();
            Notice.Text = ClipDock.Core.Localization.Text("次に保存する動画から、新しい保存先を使います。");
        }
        catch (Exception ex) { Notice.Text = ClipDock.Core.Localization.Text("保存先を選択できませんでした: ") + ex.Message; }
    }
    private void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // The trailing separator makes sure a folder is opened, never a same-named file.
            Shell.Open(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (Shell.IsOpenFailure(ex) || ex is ArgumentException or NotSupportedException)
        { Notice.Text = ClipDock.Core.Localization.Text("フォルダーを開けませんでした: ") + ex.Message; }
    }
    private void OpenOutput_Click(object? sender, RoutedEventArgs e) => OpenFolder(outputDirectory);
    private void OpenPage_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var url = DownloadRequest.IsWebUrl(item.Request.PageUrl) ? item.Request.PageUrl : item.Request.Url;
        if (!DownloadRequest.IsWebUrl(url)) return;
        try { Shell.Open(url); }
        catch (Exception ex) when (Shell.IsOpenFailure(ex)) { Notice.Text = ClipDock.Core.Localization.Text("ページを開けませんでした: ") + ex.Message; }
    }
    private void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!DownloadPlan.IsOutputFile(item.FilePath, item.OutputDirectory, item.Request.Format) || !File.Exists(item.FilePath)) { Notice.Text = ClipDock.Core.Localization.Text("ファイルが見つかりません。移動または削除されていないか確認してください。"); return; }
        try { Shell.Open(item.FilePath); }
        catch (Exception ex) when (Shell.IsOpenFailure(ex)) { Notice.Text = ClipDock.Core.Localization.Text("ファイルを開けませんでした: ") + ex.Message; }
    }
    private void ShowFile_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (DownloadPlan.IsOutputFile(item.FilePath, item.OutputDirectory, item.Request.Format) && File.Exists(item.FilePath))
        {
            try { Shell.Reveal(item.FilePath); return; }
            catch (Exception ex) when (Shell.IsOpenFailure(ex)) { }
        }
        OpenFolder(item.OutputDirectory);
    }
    private async void InstallEngine_Click(object? sender, RoutedEventArgs e)
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
        UpdateEngineStatus();
        if (!quiet) Notice.Text = ClipDock.Core.Localization.Text("初期セットアップ中です。保存エンジンを取得しています…");
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
                else Notice.Text = ClipDock.Core.Localization.Text("初期セットアップ中: ") + message;
            }), cancellation.Token);
            checkedEngine = true;
        }
        catch (OperationCanceledException) when (quiet) { }
        catch (OperationCanceledException) { Notice.Text = shuttingDown ? ClipDock.Core.Localization.Text("エンジンの準備を中止しました。") : ClipDock.Core.Localization.Text("エンジンの取得が時間内に終わりませんでした。通信環境を確認して「保存エンジンを準備」を押してください。"); }
        // The installed engine keeps working when an update cannot be fetched.
        catch (Exception) when (quiet) { }
        catch (Exception ex) { Notice.Text = ClipDock.Core.Localization.Text("エンジンの取得に失敗しました: ") + ex.Message; }
        finally
        {
            // At login the network may not be ready yet: a missing engine is retried within minutes, a failed update within half an hour.
            nextEngineCheck = DateTime.UtcNow + (checkedEngine ? EngineRecheck : quiet ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5));
            installCancellation = null; installing = false; updatingQuietly = false; EngineButton.IsEnabled = !shuttingDown; UpdateEngineStatus(); StartQueue();
        }
        if (!quiet && ToolInstaller.IsReady(ToolInstaller.FindTools())) Notice.Text = ClipDock.Core.Localization.Text("初期セットアップが完了しました。URLを貼り付けて「保存」を押してください。");
    }
    private void UpdateEngineStatus()
    {
        var ready = ToolInstaller.IsReady(ToolInstaller.FindTools());
        EngineStatus.Text = ready ? ClipDock.Core.Localization.Text("● エンジンの準備完了") : installing ? ClipDock.Core.Localization.Text("○ 初期セットアップ中…") : ClipDock.Core.Localization.Text("○ 保存エンジンを取得できていません");
        // Only a failed download leaves something to press; updates run by themselves at launch.
        EngineBanner.IsVisible = !ready && !installing;
    }

    private static void Select(ComboBox box, string? tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>()) if ((string?)item.Tag == tag) box.SelectedItem = item;
    }

    private void LoadState()
    {
        // Settings and history are read separately, and history entry by entry:
        // one unreadable file or record must not discard everything else.
        try
        {
            if (File.Exists(settingsFile) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile), Json.Options) is { } settings)
            {
                if (settings.OutputDirectory is { Length: > 0 } directory && Path.IsPathFullyQualified(directory)) outputDirectory = directory;
                Select(QualityBox, settings.Quality);
                if (settings.Format == "mp3") Mp3Option.IsChecked = true;
                UseLoginBox.IsChecked = settings.UseLogin;
                startAtLogin = settings.StartAtLogin;
                trayNoticeShown = settings.TrayNoticeShown;
            }
        }
        catch (Exception) { Notice.Text = ClipDock.Core.Localization.Text("以前の設定を読み込めませんでした。既定の設定で続行します。"); }
        try
        {
            if (!File.Exists(historyFile)) return;
            library.Restore(JsonSerializer.Deserialize<List<SavedDownload?>>(HistoryFile.Read(historyFile), Json.Options) ?? []);
        }
        catch (Exception) { Notice.Text = ClipDock.Core.Localization.Text("以前の履歴を読み込めませんでした。新しい履歴で続行します。"); }
    }
    private bool SaveState()
    {
        // Restoring the saved selections raises change events before the window is ready.
        if (!loaded) return false;
        try
        {
            // The browser overlay is a Windows feature; its setting is carried through unchanged.
            HistoryFile.Write(settingsFile, JsonSerializer.SerializeToUtf8Bytes(new AppSettings(outputDirectory, Quality, Format, false, UseLogin, SelectedLanguage, startAtLogin, trayNoticeShown), Json.Options), plain: true);
            HistoryFile.Write(historyFile, JsonSerializer.SerializeToUtf8Bytes(library.Snapshot(), Json.Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Notice.Text = ClipDock.Core.Localization.Text("履歴を保存できませんでした: ") + ex.Message; return false; }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose) return;
        // The close button (and ⌘W) hides the window; downloads and the daily engine check go on in the menu bar.
        var exiting = exitRequested || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
        exitRequested = false;
        if (!exiting && !shuttingDown) { e.Cancel = true; HideToMenuBar(); return; }
        // A shutdown started by the lifetime ends the process itself; one started here ends it through App.Quit.
        var quitHere = e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown);
        var hasWork = installing && !updatingQuietly || library.HasUnfinished;
        if (!hasWork && !installing && library.ActiveOperations == 0)
        {
            shuttingDown = true; engineTimer.Stop(); SaveState(); allowClose = true;
            if (quitHere) App.Quit();
            return;
        }
        e.Cancel = true;
        if (shuttingDown) return;
        // The engine download can take many minutes on a slow line; it is cancellable like any other work.
        var question = installing ? ClipDock.Core.Localization.Text("エンジンの準備を中止して終了しますか？\n終了しない場合、準備はメニューバーで続きます。")
            : ClipDock.Core.Localization.Text("ダウンロードを中断して終了しますか？\n終了しない場合、保存はメニューバーで続きます。");
        if (hasWork && !await ConfirmDialog.ShowAsync(this, "ClipDock", question, ClipDock.Core.Localization.Text("終了する"), ClipDock.Core.Localization.Text("続ける"))) return;
        shuttingDown = true;
        engineTimer.Stop();
        installCancellation?.Cancel();
        var stopDownloads = library.StopAsync();
        try
        {
            if (installTask is not null) await installTask;
            await stopDownloads;
        }
        catch (Exception) { /* The window closes either way; the state below is still saved. */ }
        SaveState(); allowClose = true; Close(); App.Quit();
    }

    // Automated window check (--smoke): proves on a real desktop that the window opens, the sign-in session
    // exists, a page loads in the sign-in window and its address can be read back, and that closing the window
    // keeps ClipDock in the menu bar. Stays open briefly for a screenshot, then quits as the menu bar icon's Quit does.
    private async Task RunSmokeAsync(string resultFile)
    {
        var result = new Dictionary<string, object?> { ["version"] = App.Version, ["language"] = Localization.Language, ["passed"] = false };
        try
        {
            // With --background (the login item) only the menu bar icon is there until its menu opens the window.
            result["background"] = App.StartHidden;
            result["startedHidden"] = !IsVisible;
            result["menuBarIcon"] = menuBarIcon.IsVisible;
            if (App.StartHidden) ShowFromMenuBar();
            await browserSession.WhenReadyAsync().WaitAsync(TimeSpan.FromSeconds(30));
            result["sessionStore"] = SessionStore.AdapterInfo?.ToString();
            result["status"] = await browserSession.RefreshStatusAsync("https://example.com/");
            var loadedPage = new TaskCompletionSource<(string Url, bool Success)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var login = browserSession.Show("https://example.com/", () => false, _ => { });
            login.PageLoaded += (url, success) => loadedPage.TrySetResult((url, success));
            var page = await loadedPage.Task.WaitAsync(TimeSpan.FromSeconds(45));
            result["pageUrl"] = page.Url;
            result["pageLoaded"] = page.Success;
            result["scriptUrl"] = await login.CurrentUrlAsync();
            using var cookies = await browserSession.CreateCookieFileAsync(new DownloadRequest { Url = "https://example.com/" }.Validated(), CancellationToken.None);
            result["cookieFile"] = cookies is not null;
            result["localizedControls"] = ListTitle.Text == Localization.Text("保存したファイル") &&
                login.Title == Localization.Text("ClipDock — サイトにログイン") && SelectedLanguage == Localization.Language;
            // The close button only hides the window (and the sign-in window); the menu bar icon brings it back.
            Close();
            await Task.Delay(TimeSpan.FromSeconds(1));
            result["hidesToMenuBar"] = !IsVisible && !login.IsVisible && !shuttingDown && menuBarIcon.IsVisible;
            ShowFromMenuBar();
            await Task.Delay(TimeSpan.FromSeconds(1));
            result["reopensFromMenuBar"] = IsVisible;
            result["passed"] = page.Success && (string?)result["scriptUrl"] == "https://example.com/" && result["localizedControls"] is true &&
                (bool)result["startedHidden"]! == App.StartHidden && result["menuBarIcon"] is true &&
                result["hidesToMenuBar"] is true && result["reopensFromMenuBar"] is true;
        }
        catch (Exception ex) { result["error"] = ex.ToString(); }
        SaveState();
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(result, Json.Options));
        await Task.Delay(TimeSpan.FromSeconds(12));
        // Nothing is unfinished, so this ends the process without a question; scripts/test-mac.sh waits for that.
        RequestExit();
    }
}
