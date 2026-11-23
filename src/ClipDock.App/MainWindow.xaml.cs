using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipDock.Core;
using Localization = ClipDock.Core.Localization;
using Microsoft.Win32;

namespace ClipDock.App;

public partial class MainWindow : Window
{
    private static readonly Brush SelectedFilter = new SolidColorBrush(Color.FromRgb(0xD9, 0xEE, 0xDF));
    private readonly DownloadLibrary library = new();
    private readonly DispatcherTimer activationTimer;
    private readonly string historyFile = Path.Combine(AppPaths.Data, "history.dat");
    private readonly string settingsFile = Path.Combine(AppPaths.Data, "settings.json");
    private readonly ICollectionView view;
    private string outputDirectory = AppPaths.DefaultOutput;
    private string filter = "all";
    private Task? installTask;
    private CancellationTokenSource? installCancellation;
    private bool installing;
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

    public MainWindow(EventWaitHandle activation)
    {
        Localization.Initialize(Localization.ReadPreference(settingsFile));
        foreach (var resource in Localization.Resources) System.Windows.Application.Current.Resources[resource.Key] = resource.Value;
        InitializeComponent();
        trayIcon = new TrayIcon(ShowFromTray, RequestExit);
        VersionLabel.Text = $"ClipDock  {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}  /  Windows";
        Directory.CreateDirectory(AppPaths.Data);
        var oldSessions = Path.Combine(AppPaths.Data, "sessions");
        if (Directory.Exists(oldSessions))
            foreach (var file in Directory.EnumerateFiles(oldSessions, "*.cookies"))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        view = CollectionViewSource.GetDefaultView(library.Jobs);
        view.Filter = value => value is DownloadItem item && DownloadLibrary.Shows(item, filter);
        JobList.ItemsSource = view;
        library.StateChanged += RefreshSummary;
        library.Settled += _ => DownloadSettled();
        LoadState();
        ApplyStartupAtLaunch();
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
        ConcurrencyBox.SelectionChanged += Concurrency_Changed;
        QualityBox.SelectionChanged += (_, _) => SaveState();
        FormatBox.SelectionChanged += (_, _) => { UpdateFormat(); SaveState(); };
        BrowserOverlayBox.Checked += BrowserOverlay_Changed;
        BrowserOverlayBox.Unchecked += BrowserOverlay_Changed;
        UseLoginBox.Checked += LoginPreference_Changed;
        UseLoginBox.Unchecked += LoginPreference_Changed;
        // Not tied to Loaded: at sign-in the window stays hidden, and the engine is still checked at launch.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            installTask = InstallEngineAsync();
            try { await browserSession.RefreshStatusAsync(); }
            catch (Exception ex) { LoginStatus.Text = ex.Message; }
        }, DispatcherPriority.Background);
        UpdateFormat();
        Closing += OnClosing;
        Closed += (_, _) => { engineTimer.Stop(); trayIcon.Dispose(); browserOverlay.Dispose(); browserSession.Dispose(); };
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
        if (trayNoticeShown) return;
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
            Notice.Text = Localization.Text("Windowsの起動設定を変更できませんでした: ") + ex.Message;
            return;
        }
        startAtLogin = enable;
        SaveState();
        Notice.Text = enable ? Localization.Text("Windowsへのサインイン時に、ClipDockを通知領域で開始します。") : Localization.Text("Windowsの起動時にはClipDockを開始しません。");
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (shuttingDown || LanguageBox.SelectedItem is not ComboBoxItem) return;
        if (SaveState()) Notice.Text = Localization.Text("言語設定を保存しました。アプリを開き直すと適用されます。", SelectedLanguage);
    }
    private string SelectedLanguage => Localization.Normalize((LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string);

    private string Quality => (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1080";
    private string Format => (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "mp4";
    private void UpdateFormat() => QualityBox.IsEnabled = Format == "mp4";
    private void LoginPreference_Changed(object sender, RoutedEventArgs e)
    {
        SaveState();
        Notice.Text = UseLoginBox.IsChecked == true ? ClipDock.Core.Localization.Text("接続済みサイトのログイン情報を保存に使います。初回は「ログイン」から接続してください。") : ClipDock.Core.Localization.Text("次の保存・再試行ではログイン情報を使いません。");
    }
    private void Login_Click(object sender, RoutedEventArgs e) => browserSession.Show(UrlInput.Text.Trim() is { Length: > 0 } url ? url : library.Jobs.FirstOrDefault()?.Request.Url,
        () => library.HasUnfinished, url => { UrlInput.Text = url; Notice.Text = ClipDock.Core.Localization.Text("URLと保存先を確認して「保存」を押してください。"); Activate(); });
    private void BrowserOverlay_Changed(object sender, RoutedEventArgs e)
    {
        browserOverlay.Enabled = BrowserOverlayBox.IsChecked == true;
        SaveState();
        Notice.Text = browserOverlay.Enabled
            ? ClipDock.Core.Localization.Text("Chrome / Edgeの上中央にマウスを置くと、保存タブが表示されます。クリックしてURLを取り込んでください。")
            : ClipDock.Core.Localization.Text("ブラウザー上部の保存タブを非表示にしました。");
    }
    private void ReceiveBrowserAddress(BrowserAddressResult result)
    {
        if (shuttingDown) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
        if (result.Url is { } url)
        {
            UrlInput.Text = url;
            Notice.Text = result.AddedScheme
                ? ClipDock.Core.Localization.Text("ブラウザーの表示に https:// を補いました。URLを確認して「保存」を押してください。")
                : ClipDock.Core.Localization.Text("URLを確認して「保存」を押してください。");
        }
        else Notice.Text = Localization.Text(result.Message);
        UrlInput.Focus();
        UrlInput.SelectAll();
    }
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
        library.Start((item, ct) => DownloadEngine.RunAsync(item, tools, action => Dispatcher.Invoke(action), ct,
            (request, token) => Dispatcher.InvokeAsync(() => browserSession.CreateCookieFileAsync(request, token)).Task.Unwrap()));
        EngineButton.IsEnabled = library.ActiveOperations == 0;
    }

    private void DownloadSettled()
    {
        EngineButton.IsEnabled = !shuttingDown && !installing && library.ActiveOperations == 0;
        if (!shuttingDown) SaveState();
        RefreshSummary();
    }

    private void Concurrency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (shuttingDown || ConcurrencyBox.SelectedItem is not ComboBoxItem choice || !int.TryParse(choice.Tag as string, out var count)) return;
        library.MaxConcurrency = count;
        SaveState(); StartQueue(); RefreshSummary();
        Notice.Text = ClipDock.Core.Localization.Format($"同時に保存する動画を{count}件に設定しました。進行中の保存は中断しません。");
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddUrl();
    private void UrlInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddUrl(); e.Handled = true; } }
    private void AddUrl()
    {
        try { Add(new DownloadRequest { Url = UrlInput.Text.Trim(), PageUrl = UrlInput.Text.Trim(), Quality = Quality, Format = Format, UseLogin = UseLoginBox.IsChecked == true }); UrlInput.Clear(); }
        catch (Exception ex) { Notice.Text = ex.Message; }
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
        var retried = library.Retry(item, UseLoginBox.IsChecked == true, out var message);
        if (message is not null) Notice.Text = message;
        if (!retried) return;
        SaveState(); StartQueue();
    }
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        filter = (sender as FrameworkElement)?.Tag as string ?? "all";
        foreach (var button in new[] { AllFilter, ActiveFilter, CompletedFilter })
            button.Background = (string)button.Tag == filter ? SelectedFilter : Brushes.Transparent;
        RefreshSummary();
    }
    private void RefreshSummary()
    {
        view.Refresh();
        CountLabel.Text = library.CountText(filter, view.Cast<object>().Count());
        QueueStatus.Text = library.QueueText();
        trayIcon.Text = library.Jobs.Any(j => j.IsBusy || j.State == "queued") ? "ClipDock\n" + QueueStatus.Text : "ClipDock";
        EmptyState.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = ClipDock.Core.Localization.Text("動画の保存先"), InitialDirectory = Directory.Exists(outputDirectory) ? outputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (dialog.ShowDialog(this) != true) return;
        outputDirectory = dialog.FolderName; OutputLabel.Text = outputDirectory; SaveState();
        Notice.Text = ClipDock.Core.Localization.Text("次に保存する動画から、新しい保存先を使います。");
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
        { Notice.Text = ClipDock.Core.Localization.Text("フォルダーを開けませんでした: ") + ex.Message; }
    }
    private void OpenOutput_Click(object sender, RoutedEventArgs e) => OpenFolder(outputDirectory);
    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        var url = DownloadRequest.IsWebUrl(item.Request.PageUrl) ? item.Request.PageUrl : item.Request.Url;
        if (!DownloadRequest.IsWebUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception ex) { Notice.Text = ClipDock.Core.Localization.Text("ページを開けませんでした: ") + ex.Message; }
    }
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (!DownloadPlan.IsOutputFile(item.FilePath, item.OutputDirectory, item.Request.Format) || !File.Exists(item.FilePath)) { Notice.Text = ClipDock.Core.Localization.Text("ファイルが見つかりません。移動または削除されていないか確認してください。"); return; }
        try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        { Notice.Text = ClipDock.Core.Localization.Text("ファイルを開けませんでした: ") + ex.Message; }
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
            // At sign-in the network may not be ready yet: a missing engine is retried within minutes, a failed update within half an hour.
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
        EngineButton.Visibility = ready || installing ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        library.ClearFinished();
        SaveState(); RefreshSummary(); Notice.Text = ClipDock.Core.Localization.Text("完了・失敗・キャンセルの履歴を消去しました。保存した動画ファイルはそのままです。");
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
                foreach (ComboBoxItem quality in QualityBox.Items) if ((string)quality.Tag == settings.Quality) QualityBox.SelectedItem = quality;
                foreach (ComboBoxItem format in FormatBox.Items) if ((string)format.Tag == settings.Format) FormatBox.SelectedItem = format;
                BrowserOverlayBox.IsChecked = settings.BrowserOverlayEnabled;
                UseLoginBox.IsChecked = settings.UseLogin;
                startAtLogin = settings.StartAtLogin;
                trayNoticeShown = settings.TrayNoticeShown;
                library.MaxConcurrency = Math.Clamp(settings.MaxConcurrentDownloads, 1, DownloadScheduler.MaximumConcurrency);
                foreach (ComboBoxItem count in ConcurrencyBox.Items) if ((string)count.Tag == library.MaxConcurrency.ToString()) ConcurrencyBox.SelectedItem = count;
            }
        }
        catch (Exception) { Notice.Text = ClipDock.Core.Localization.Text("以前の設定を読み込めませんでした。既定の設定で続行します。"); }
        try
        {
            if (!File.Exists(historyFile)) return;
            library.Restore(JsonSerializer.Deserialize<List<SavedDownload?>>(WindowsProtection.Unprotect(File.ReadAllBytes(historyFile)), Json.Options) ?? []);
        }
        catch (Exception) { Notice.Text = ClipDock.Core.Localization.Text("以前の履歴を読み込めませんでした。新しい履歴で続行します。"); }
    }
    private bool SaveState()
    {
        try
        {
            File.WriteAllText(settingsFile + ".tmp", JsonSerializer.Serialize(new AppSettings(outputDirectory, Quality, library.MaxConcurrency, Format, BrowserOverlayBox.IsChecked == true, UseLoginBox.IsChecked == true, SelectedLanguage, startAtLogin, trayNoticeShown), Json.Options));
            File.Move(settingsFile + ".tmp", settingsFile, true);
            File.WriteAllBytes(historyFile + ".tmp", WindowsProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(library.Snapshot(), Json.Options)));
            File.Move(historyFile + ".tmp", historyFile, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Notice.Text = ClipDock.Core.Localization.Text("履歴を保存できませんでした: ") + ex.Message; return false; }
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
        if (hasWork && MessageBox.Show(this, question, "ClipDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
