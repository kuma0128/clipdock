using System.Collections.ObjectModel;
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
using Microsoft.Win32;

namespace ClipDock.App;

public partial class MainWindow : Window
{
    private static readonly Brush SelectedFilter = new SolidColorBrush(Color.FromRgb(0x2B, 0x42, 0x38));
    private readonly ObservableCollection<DownloadItem> jobs = [];
    private readonly DispatcherTimer inboxTimer;
    private readonly string historyFile = Path.Combine(AppPaths.Data, "history.dat");
    private readonly string settingsFile = Path.Combine(AppPaths.Data, "settings.json");
    private readonly ICollectionView view;
    private string outputDirectory = AppPaths.DefaultOutput;
    private string filter = "all";
    private Task? worker;
    private Task? installTask;
    private CancellationTokenSource? installCancellation;
    private bool installing;
    private bool shuttingDown;
    private bool allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(AppPaths.Data);
        Directory.CreateDirectory(AppPaths.Inbox);
        // Remove leftover plaintext cookie jars from a previous crash before accepting work.
        if (Directory.Exists(AppPaths.Secrets))
            foreach (var file in Directory.EnumerateFiles(AppPaths.Secrets, "*.cookies"))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        // A sender that died between writing and renaming leaves a .tmp behind; a live one finishes within seconds.
        foreach (var file in Directory.EnumerateFiles(AppPaths.Inbox, "*.tmp"))
            try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(10)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        view = CollectionViewSource.GetDefaultView(jobs);
        view.Filter = value => value is DownloadItem item && (filter == "all" || filter == "active" && item.CanCancel || filter == "completed" && item.IsCompleted);
        JobList.ItemsSource = view;
        LoadState();
        OutputLabel.Text = outputDirectory;
        RefreshSummary();
        UpdateEngineStatus();
        inboxTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        inboxTimer.Tick += (_, _) => ReadInbox();
        inboxTimer.Start();
        Loaded += (_, _) => ReadInbox();
        Closing += OnClosing;
    }

    private string Quality => (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1080";
    private void Add(DownloadRequest source)
    {
        var request = source.Validated();
        if (request.Action != "download") return;
        if (jobs.Count(j => j.CanCancel) >= 100) throw new InvalidOperationException("待機中の動画は100件までです。");
        if (jobs.FirstOrDefault(j => j.CanCancel && j.Request.Url == request.Url && j.Request.Quality == request.Quality) is { } existing)
        {
            // Sending again with login information must not be lost just because the first click is still waiting.
            var refreshed = existing.State == "queued" && request.Cookies.Count > 0;
            if (refreshed) existing.Request = request;
            Notice.Text = refreshed ? "この動画はすでに待機中です。ログイン情報を更新しました。" : "この動画はすでにダウンロード一覧に追加されています。";
            return;
        }
        var item = new DownloadItem { Request = request, Title = request.Title.Length > 0 ? request.Title : new Uri(request.Url).Host, OutputDirectory = outputDirectory };
        Attach(item);
        jobs.Insert(0, item);
        Notice.Text = ToolInstaller.IsReady(ToolInstaller.FindTools()) ? "ダウンロード一覧に追加しました。" : "動画を待機リストに追加しました。「エンジンを準備 / 更新」を押すと開始します。";
        SaveState(); RefreshSummary(); StartQueue();
    }

    private void Attach(DownloadItem item) => item.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName == nameof(DownloadItem.State)) RefreshSummary();
    };

    private void StartQueue()
    {
        if (shuttingDown || installing || worker is { IsCompleted: false } || !ToolInstaller.IsReady(ToolInstaller.FindTools())) return;
        worker = ProcessQueue();
    }

    private async Task ProcessQueue()
    {
        EngineButton.IsEnabled = false;
        try
        {
            while (!shuttingDown && jobs.LastOrDefault(j => j.State == "queued") is { } item)
            {
                await DownloadEngine.RunAsync(item, action => Dispatcher.Invoke(action));
                SaveState();
            }
        }
        finally { EngineButton.IsEnabled = true; RefreshSummary(); }
    }

    private void ReadInbox()
    {
        if (shuttingDown) return;
        List<string> files;
        // This runs on a timer: if the data folder disappears, an unhandled error here would open a new dialog on every tick.
        try { files = Directory.EnumerateFiles(AppPaths.Inbox, "*.request").OrderBy(File.GetCreationTimeUtc).Take(12).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var file in files)
        {
            try
            {
                if (new FileInfo(file).Length > NativeProtocol.MaxMessageBytes + 4096) throw new InvalidDataException("リクエストが大きすぎます。");
                var bytes = WindowsProtection.Unprotect(File.ReadAllBytes(file));
                var request = (JsonSerializer.Deserialize<DownloadRequest>(bytes, Json.Options) ?? throw new InvalidDataException()).Validated();
                if (request.Action == "download") Add(request);
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Notice.Text = "Chromeからの受信に失敗しました: " + ex.Message;
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddUrl();
    private void UrlInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddUrl(); e.Handled = true; } }
    private void AddUrl()
    {
        try { Add(new DownloadRequest { Url = UrlInput.Text.Trim(), PageUrl = UrlInput.Text.Trim(), Quality = Quality }); UrlInput.Clear(); }
        catch (Exception ex) { Notice.Text = ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (item.State == "queued") { item.State = "cancelled"; item.Detail = "待機を取り消しました。"; item.Request = item.Request with { Cookies = [] }; }
        else item.Cancellation?.Cancel();
        SaveState();
    }
    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item || !item.CanRetry) return;
        item.Percent = 0; item.State = "queued"; item.Detail = "再試行を待っています";
        Notice.Text = "ログイン情報が必要な動画は、Chrome拡張からもう一度送信してください。";
        SaveState(); StartQueue();
    }
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        filter = (sender as FrameworkElement)?.Tag as string ?? "all";
        // Move the "selected" tint with the choice; it used to stay on the first entry.
        foreach (var button in new[] { AllFilter, ActiveFilter, CompletedFilter })
            button.Background = (string)button.Tag == filter ? SelectedFilter : Brushes.Transparent;
        RefreshSummary();
    }
    private void RefreshSummary()
    {
        view.Refresh();
        CountLabel.Text = $"{(filter == "active" ? "ダウンロード中" : filter == "completed" ? "保存した動画" : "ライブラリ")}  /  {view.Cast<object>().Count()} 件     ·     保存完了 {jobs.Count(j => j.IsCompleted)} 件";
        EmptyState.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "動画の保存先", InitialDirectory = Directory.Exists(outputDirectory) ? outputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (dialog.ShowDialog(this) != true) return;
        outputDirectory = dialog.FolderName; OutputLabel.Text = outputDirectory; SaveState();
        Notice.Text = "次に追加する動画から、新しい保存先を使います。";
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
        { Notice.Text = "フォルダーを開けませんでした: " + ex.Message; }
    }
    private void OpenOutput_Click(object sender, RoutedEventArgs e) => OpenFolder(outputDirectory);
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (!DownloadPlan.IsOutputFile(item.FilePath, item.OutputDirectory) || !File.Exists(item.FilePath)) { Notice.Text = "動画が見つかりません。移動または削除されていないか確認してください。"; return; }
        Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
    }
    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (DownloadPlan.IsOutputFile(item.FilePath, item.OutputDirectory) && File.Exists(item.FilePath))
        {
            // Highlight the saved file. /select needs this exact quoted form; Windows file names cannot contain quotes.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            try { Process.Start(new ProcessStartInfo(explorer, $"/select,\"{Path.GetFullPath(item.FilePath)}\"") { UseShellExecute = false })?.Dispose(); return; }
            catch (Win32Exception) { }
        }
        OpenFolder(item.OutputDirectory);
    }
    private void ExtensionFolder_Click(object sender, RoutedEventArgs e)
    {
        var extension = Path.Combine(AppContext.BaseDirectory, "extension");
        if (!Directory.Exists(extension)) { Notice.Text = "拡張フォルダーがありません。scripts/build.ps1で配布用アプリを作成してください。"; return; }
        OpenFolder(extension);
        Notice.Text = "chrome://extensions でデベロッパーモードをON →「パッケージ化されていない拡張機能を読み込む」→ 今開いたextensionフォルダーを選択してください。";
    }
    private void Register_Click(object sender, RoutedEventArgs e)
    {
        var host = Path.Combine(AppContext.BaseDirectory, "ClipDock.NativeHost.exe");
        if (!File.Exists(host)) { Notice.Text = "NativeHostがありません。配布フォルダー内のClipDock.exeを起動してください。"; return; }
        try
        {
            var manifest = Path.Combine(AppPaths.Data, "native-host.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new { name = Integration.HostName, description = "ClipDock Chrome bridge", path = host, type = "stdio", allowed_origins = new[] { Integration.Origin } }, Json.Options));
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Google\Chrome\NativeMessagingHosts\" + Integration.HostName);
            key.SetValue("", manifest);
            Notice.Text = "Chrome連携を設定しました。次に「拡張フォルダーを開く」を押して、Chromeに拡張を読み込んでください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Notice.Text = "Chrome連携を設定できませんでした: " + ex.Message; }
    }
    private async void InstallEngine_Click(object sender, RoutedEventArgs e)
    {
        if (worker is { IsCompleted: false } || installing || shuttingDown) return;
        installTask = InstallEngineAsync();
        await installTask;
    }
    // Never throws: closing the window awaits this task to know the download has really stopped.
    private async Task InstallEngineAsync()
    {
        installing = true; EngineButton.IsEnabled = false;
        using var cancellation = installCancellation = new CancellationTokenSource();
        try
        {
            await ToolInstaller.InstallAsync(AppPaths.Tools, new Progress<string>(message => { EngineStatus.Text = message; Notice.Text = message; }), cancellation.Token);
            Notice.Text = "エンジンの準備ができました。待機中の動画をダウンロードします。";
        }
        catch (OperationCanceledException) { Notice.Text = cancellation.IsCancellationRequested ? "エンジンの準備を中止しました。" : "エンジンの取得が時間内に終わりませんでした。通信環境を確認して再実行してください。"; }
        catch (Exception ex) { Notice.Text = "エンジンの取得に失敗しました: " + ex.Message; }
        finally { installCancellation = null; installing = false; EngineButton.IsEnabled = true; UpdateEngineStatus(); StartQueue(); }
    }
    private void UpdateEngineStatus() => EngineStatus.Text = ToolInstaller.IsReady(ToolInstaller.FindTools()) ? "● エンジンの準備完了" : "○ 初回はエンジンを準備してください";
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in jobs.Where(j => !j.CanCancel).ToList()) jobs.Remove(job);
        SaveState(); RefreshSummary(); Notice.Text = "完了・失敗・キャンセルの履歴を消去しました。保存した動画ファイルはそのままです。";
    }
    private void LoadState()
    {
        // Settings and history are read separately, and history entry by entry:
        // one unreadable file or record must not discard everything else.
        try
        {
            if (File.Exists(settingsFile) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile), Json.Options) is { } settings)
            {
                if (settings.OutputDirectory is { Length: > 0 } directory && Path.IsPathFullyQualified(directory)) outputDirectory = directory;
                foreach (ComboBoxItem quality in QualityBox.Items) if ((string)quality.Tag == settings.Quality) QualityBox.SelectedItem = quality;
            }
        }
        catch (Exception) { Notice.Text = "以前の設定を読み込めませんでした。既定の設定で続行します。"; }
        try
        {
            if (!File.Exists(historyFile)) return;
            var saved = JsonSerializer.Deserialize<List<SavedDownload?>>(WindowsProtection.Unprotect(File.ReadAllBytes(historyFile)), Json.Options) ?? [];
            foreach (var item in saved.Take(300))
            {
                if (item?.Request is null || !Guid.TryParseExact(item.Id, "N", out _) ||
                    item.OutputDirectory is not { Length: > 0 } || !Path.IsPathFullyQualified(item.OutputDirectory)) continue;
                DownloadRequest request;
                try { request = item.Request.Validated() with { Cookies = [] }; }
                catch (ArgumentException) { continue; }
                var terminal = item.State is "completed" or "failed" or "cancelled";
                var job = new DownloadItem { Id = item.Id, Request = request, CreatedAt = item.CreatedAt,
                    OutputDirectory = item.OutputDirectory, Title = item.Title ?? "", State = terminal ? item.State : "interrupted", FilePath = item.FilePath ?? "",
                    Detail = terminal ? item.Detail ?? "" : "前回の終了で中断しました。再試行するか、Chromeから送り直してください。", Percent = item.State == "completed" ? 100 : 0 };
                Attach(job); jobs.Add(job);
            }
        }
        catch (Exception) { Notice.Text = "以前の履歴を読み込めませんでした。新しい履歴で続行します。"; }
    }
    private void SaveState()
    {
        try
        {
            File.WriteAllText(settingsFile + ".tmp", JsonSerializer.Serialize(new Settings(outputDirectory, Quality), Json.Options));
            File.Move(settingsFile + ".tmp", settingsFile, true);
            File.WriteAllBytes(historyFile + ".tmp", WindowsProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(jobs.Take(300).Select(j => j.Save()).ToList(), Json.Options)));
            File.Move(historyFile + ".tmp", historyFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Notice.Text = "履歴を保存できませんでした: " + ex.Message; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        if (!installing && !jobs.Any(j => j.CanCancel)) { inboxTimer.Stop(); SaveState(); return; }
        e.Cancel = true;
        if (shuttingDown) return;
        // The engine download can take many minutes on a slow line; it is cancellable like any other work.
        var question = installing ? "エンジンの準備を中止して終了しますか？\n続ける場合は、このウィンドウを最小化してください。"
            : "ダウンロードを中断して終了しますか？\n続ける場合は、このウィンドウを最小化してください。";
        if (MessageBox.Show(this, question, "ClipDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        shuttingDown = true;
        inboxTimer.Stop();
        installCancellation?.Cancel();
        foreach (var item in jobs.Where(j => j.State == "queued").ToList()) { item.State = "interrupted"; item.Request = item.Request with { Cookies = [] }; }
        foreach (var item in jobs.Where(j => j.IsBusy).ToList()) item.Cancellation?.Cancel();
        try
        {
            if (installTask is not null) await installTask;
            if (worker is not null) await worker;
        }
        catch (Exception) { /* The window closes either way; the state below is still saved. */ }
        SaveState(); allowClose = true; Close();
    }
}
