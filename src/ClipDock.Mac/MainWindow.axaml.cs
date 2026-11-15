using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
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
    private static readonly IBrush SelectedFilter = new SolidColorBrush(Color.Parse("#D9EEDF"));
    private readonly ObservableCollection<DownloadItem> jobs = [];
    // What the list shows: jobs narrowed by the selected filter.
    private readonly ObservableCollection<DownloadItem> visibleJobs = [];
    private readonly string historyFile = Path.Combine(AppPaths.Data, "history.dat");
    private readonly string settingsFile = Path.Combine(AppPaths.Data, "settings.json");
    private string outputDirectory = AppPaths.DefaultOutput;
    private string filter = "all";
    private readonly DownloadScheduler scheduler = new();
    private readonly Dictionary<DownloadItem, DownloadOperation> operations = [];
    private readonly HashSet<DownloadItem> closingJobs = [];
    private Task? installTask;
    private CancellationTokenSource? installCancellation;
    private bool installing;
    private bool shuttingDown;
    private bool allowClose;
    private bool loaded;
    private readonly BrowserSession browserSession;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"ClipDock  {App.Version}  /  {(OperatingSystem.IsMacOS() ? "macOS" : "Avalonia")}";
        HistoryFile.PrepareDataDirectory();
        var oldSessions = Path.Combine(AppPaths.Data, "sessions");
        if (Directory.Exists(oldSessions))
            foreach (var file in Directory.EnumerateFiles(oldSessions, "*.cookies"))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        JobList.ItemsSource = visibleJobs;
        LoadState();
        loaded = true;
        browserSession = new BrowserSession(this, SessionStore);
        browserSession.StatusChanged += message => LoginStatus.Text = message;
        OutputLabel.Text = outputDirectory;
        ToolTip.SetTip(OutputLabel, outputDirectory);
        RefreshSummary();
        UpdateEngineStatus();
        ConcurrencyBox.SelectionChanged += Concurrency_Changed;
        QualityBox.SelectionChanged += (_, _) => SaveState();
        FormatBox.SelectionChanged += (_, _) => { UpdateFormat(); SaveState(); };
        UseLoginBox.IsCheckedChanged += LoginPreference_Changed;
        Opened += async (_, _) =>
        {
            if (App.SmokeResultFile is { } result) { await RunSmokeAsync(result); return; }
            try { await browserSession.RefreshStatusAsync(); }
            catch (Exception ex) { LoginStatus.Text = ex.Message; }
        };
        UpdateFormat();
        Closing += OnClosing;
        Closed += (_, _) => browserSession.Close();
    }

    private string Quality => (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1080";
    private string Format => (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "mp4";
    private bool UseLogin => UseLoginBox.IsChecked == true;
    private void UpdateFormat() => QualityBox.IsEnabled = Format == "mp4";
    private void LoginPreference_Changed(object? sender, RoutedEventArgs e)
    {
        SaveState();
        Notice.Text = UseLogin ? "接続済みサイトのログイン情報を保存に使います。初回は「ログイン」から接続してください。" : "次の追加・再試行ではログイン情報を使いません。";
    }
    private void Login_Click(object? sender, RoutedEventArgs e) => browserSession.Show((UrlInput.Text ?? "").Trim() is { Length: > 0 } url ? url : jobs.FirstOrDefault()?.Request.Url,
        () => jobs.Any(j => j.CanCancel), url => { UrlInput.Text = url; Notice.Text = "URLと保存先を確認して追加してください。"; Activate(); });

    private void Add(DownloadRequest source)
    {
        if (shuttingDown) throw new InvalidOperationException("終了処理中です。アプリを開き直してから追加してください。");
        var request = source.Validated();
        request.ValidateVideoPage();
        if (jobs.Any(j => j.CanCancel && j.Request.Url == request.Url && j.Request.Quality == request.Quality && j.Request.Format == request.Format))
        {
            Notice.Text = "この動画はすでにダウンロード一覧に追加されています。";
            return;
        }
        if (jobs.Count(j => j.CanCancel) >= 100) throw new InvalidOperationException("待機中の動画は100件までです。");
        var item = new DownloadItem { Request = request, Title = request.Title.Length > 0 ? request.Title : new Uri(request.Url).Host, OutputDirectory = outputDirectory };
        Attach(item);
        jobs.Insert(0, item);
        Notice.Text = ToolInstaller.IsReady(ToolInstaller.FindTools()) ? "ダウンロード一覧に追加しました。" : "待機リストに追加しました。「保存エンジンを準備」を押すと開始します。";
        SaveState(); RefreshSummary(); StartQueue();
    }

    private void Attach(DownloadItem item) => item.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName == nameof(DownloadItem.State)) RefreshSummary();
    };

    private void StartQueue()
    {
        var tools = ToolInstaller.FindTools();
        if (shuttingDown || installing || !ToolInstaller.IsReady(tools)) return;
        // The list is newest first; the scheduler consumes work in insertion order.
        foreach (var item in jobs.Reverse().Where(j => j.State == "queued" && !operations.ContainsKey(j)).ToList())
        {
            var operation = scheduler.Enqueue(ct => DownloadEngine.RunAsync(item, tools, action => Dispatcher.UIThread.Invoke(action), ct,
                (request, token) => Dispatcher.UIThread.InvokeAsync(() => browserSession.CreateCookieFileAsync(request, token))));
            operations.Add(item, operation);
            _ = ObserveDownloadAsync(item, operation);
        }
        EngineButton.IsEnabled = operations.Count == 0;
    }

    private async Task ObserveDownloadAsync(DownloadItem item, DownloadOperation operation)
    {
        try { await operation.Completion; }
        catch (OperationCanceledException)
        {
            // Queued work can be cancelled without invoking the download engine at all.
            item.State = shuttingDown ? "interrupted" : "cancelled";
            item.Detail = shuttingDown ? "終了に伴い待機を中断しました。" : "待機を取り消しました。";
        }
        catch (Exception ex) { item.State = "failed"; item.Detail = ex.Message; }
        finally
        {
            operations.Remove(item);
            EngineButton.IsEnabled = !shuttingDown && !installing && operations.Count == 0;
            if (!shuttingDown) SaveState();
            RefreshSummary();
        }
    }

    private void Concurrency_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (shuttingDown || ConcurrencyBox.SelectedItem is not ComboBoxItem choice || !int.TryParse(choice.Tag as string, out var count)) return;
        scheduler.MaxConcurrency = count;
        SaveState(); StartQueue(); RefreshSummary();
        Notice.Text = $"同時に保存する動画を{count}件に設定しました。進行中の保存は中断しません。";
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
        if (operations.TryGetValue(item, out var operation)) { item.Detail = "キャンセルしています…"; operation.Cancel(); }
        else if (item.State == "queued") { item.State = "cancelled"; item.Detail = "待機を取り消しました。"; }
        SaveState();
    }
    private void Retry_Click(object? sender, RoutedEventArgs e)
    {
        if (shuttingDown || ItemOf(sender) is not { } item || !item.CanRetry || operations.ContainsKey(item)) return;
        try { item.Request.ValidateVideoPage(); }
        catch (ArgumentException ex) { Notice.Text = ex.Message; return; }
        if (jobs.Any(j => j.CanCancel && j.Request.Url == item.Request.Url && j.Request.Quality == item.Request.Quality && j.Request.Format == item.Request.Format))
        { Notice.Text = "この動画はすでにダウンロード一覧に追加されています。"; return; }
        if (jobs.Count(j => j.CanCancel) >= 100) { Notice.Text = "待機中の動画は100件までです。"; return; }
        // Retrying is a new queue insertion: wait behind work already queued and keep
        // the retried item inside the persisted history window.
        jobs.Move(jobs.IndexOf(item), 0);
        item.Request = item.Request with { UseLogin = UseLogin };
        item.Percent = 0; item.State = "queued"; item.Detail = "再試行を待っています";
        Notice.Text = item.Request.UseLogin ? "最新のログイン情報を確認して、もう一度取得します。" : "ログイン情報を使わずに、もう一度取得します。";
        SaveState(); RefreshSummary(); StartQueue();
    }
    private void Filter_Click(object? sender, RoutedEventArgs e)
    {
        filter = (sender as Control)?.Tag as string ?? "all";
        foreach (var button in new[] { AllFilter, ActiveFilter, CompletedFilter })
            button.Background = (string?)button.Tag == filter ? SelectedFilter : Brushes.Transparent;
        RefreshSummary();
    }
    private void RefreshSummary()
    {
        var shown = jobs.Where(item => filter == "all" || filter == "active" && item.CanCancel || filter == "completed" && item.IsCompleted).ToList();
        // Progress updates arrive many times a second; only touch the list when its contents really changed.
        if (!shown.SequenceEqual(visibleJobs))
        {
            visibleJobs.Clear();
            foreach (var item in shown) visibleJobs.Add(item);
        }
        CountLabel.Text = $"{(filter == "active" ? "ダウンロード中" : filter == "completed" ? "保存したファイル" : "ライブラリ")}  /  {shown.Count} 件     ·     保存完了 {jobs.Count(j => j.IsCompleted)} 件";
        QueueStatus.Text = $"保存中 {jobs.Count(j => j.IsBusy)} 件  ·  待機 {jobs.Count(j => j.State == "queued")} 件";
        EmptyState.IsVisible = shown.Count == 0;
    }
    private async void ChooseOutput_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var start = Directory.Exists(outputDirectory) ? outputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "動画の保存先", AllowMultiple = false, SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start)
            });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } chosen || !Path.IsPathFullyQualified(chosen)) return;
            outputDirectory = chosen; OutputLabel.Text = outputDirectory; ToolTip.SetTip(OutputLabel, outputDirectory); SaveState();
            Notice.Text = "次に追加する動画から、新しい保存先を使います。";
        }
        catch (Exception ex) { Notice.Text = "保存先を選択できませんでした: " + ex.Message; }
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
        { Notice.Text = "フォルダーを開けませんでした: " + ex.Message; }
    }
    private void OpenOutput_Click(object? sender, RoutedEventArgs e) => OpenFolder(outputDirectory);
    private void OpenPage_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var url = DownloadRequest.IsWebUrl(item.Request.PageUrl) ? item.Request.PageUrl : item.Request.Url;
        if (!DownloadRequest.IsWebUrl(url)) return;
        try { Shell.Open(url); }
        catch (Exception ex) when (Shell.IsOpenFailure(ex)) { Notice.Text = "ページを開けませんでした: " + ex.Message; }
    }
    private void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!DownloadPlan.IsOutputFile(item.FilePath, item.OutputDirectory, item.Request.Format) || !File.Exists(item.FilePath)) { Notice.Text = "ファイルが見つかりません。移動または削除されていないか確認してください。"; return; }
        try { Shell.Open(item.FilePath); }
        catch (Exception ex) when (Shell.IsOpenFailure(ex)) { Notice.Text = "ファイルを開けませんでした: " + ex.Message; }
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
        if (operations.Count > 0 || installing || shuttingDown) return;
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
        }
        catch (OperationCanceledException) { Notice.Text = cancellation.IsCancellationRequested ? "エンジンの準備を中止しました。" : "エンジンの取得が時間内に終わりませんでした。通信環境を確認して再実行してください。"; }
        catch (Exception ex) { Notice.Text = "エンジンの取得に失敗しました: " + ex.Message; }
        finally { installCancellation = null; installing = false; EngineButton.IsEnabled = !shuttingDown; UpdateEngineStatus(); StartQueue(); }
    }
    private void UpdateEngineStatus()
    {
        var ready = ToolInstaller.IsReady(ToolInstaller.FindTools());
        EngineStatus.Text = ready ? "● エンジンの準備完了" : "○ 初回はエンジンを準備してください";
        EngineButton.Content = ready ? "エンジンの更新を確認" : "保存エンジンを準備";
    }
    private void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var job in jobs.Where(j => !j.CanCancel && !operations.ContainsKey(j)).ToList())
        {
            try { DownloadPlan.CleanWorkDirectory(job.OutputDirectory, job.Id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            jobs.Remove(job);
        }
        SaveState(); RefreshSummary(); Notice.Text = "完了・失敗・キャンセルの履歴を消去しました。保存した動画ファイルはそのままです。";
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
                Select(FormatBox, settings.Format);
                UseLoginBox.IsChecked = settings.UseLogin;
                scheduler.MaxConcurrency = Math.Clamp(settings.MaxConcurrentDownloads, 1, DownloadScheduler.MaximumConcurrency);
                Select(ConcurrencyBox, scheduler.MaxConcurrency.ToString());
            }
        }
        catch (Exception) { Notice.Text = "以前の設定を読み込めませんでした。既定の設定で続行します。"; }
        try
        {
            if (!File.Exists(historyFile)) return;
            var saved = JsonSerializer.Deserialize<List<SavedDownload?>>(HistoryFile.Read(historyFile), Json.Options) ?? [];
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in saved.Take(300))
            {
                if (item?.Request is null || !Guid.TryParseExact(item.Id, "N", out _) ||
                    item.OutputDirectory is not { Length: > 0 } || !Path.IsPathFullyQualified(item.OutputDirectory) || !ids.Add(item.Id)) continue;
                DownloadRequest request;
                try { request = item.Request.Validated(); }
                catch (ArgumentException) { continue; }
                var terminal = item.State is "completed" or "failed" or "cancelled";
                var job = new DownloadItem { Id = item.Id, Request = request, CreatedAt = item.CreatedAt,
                    OutputDirectory = item.OutputDirectory, Title = item.Title ?? "", State = terminal ? item.State : "interrupted", FilePath = item.FilePath ?? "",
                    Detail = item.State == "failed" ? DownloadFailure.Describe(request.Url, item.Detail) : terminal ? item.Detail ?? "" : "前回の終了で中断しました。再試行するか、URLを追加し直してください。", Percent = item.State == "completed" ? 100 : 0 };
                Attach(job); jobs.Add(job);
            }
        }
        catch (Exception) { Notice.Text = "以前の履歴を読み込めませんでした。新しい履歴で続行します。"; }
    }
    private void SaveState()
    {
        // Restoring the saved selections raises change events before the window is ready.
        if (!loaded) return;
        try
        {
            // The browser overlay is a Windows feature; its setting is carried through unchanged.
            HistoryFile.Write(settingsFile, JsonSerializer.SerializeToUtf8Bytes(new AppSettings(outputDirectory, Quality, scheduler.MaxConcurrency, Format, false, UseLogin), Json.Options), plain: true);
            HistoryFile.Write(historyFile, JsonSerializer.SerializeToUtf8Bytes(DownloadHistory.Snapshot(jobs, closingJobs), Json.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Notice.Text = "履歴を保存できませんでした: " + ex.Message; }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose) return;
        var hasWork = installing || jobs.Any(j => j.CanCancel);
        if (!hasWork && operations.Count == 0) { SaveState(); return; }
        e.Cancel = true;
        if (shuttingDown) return;
        // The engine download can take many minutes on a slow line; it is cancellable like any other work.
        var question = installing ? "エンジンの準備を中止して終了しますか？\n続ける場合は、このウィンドウをしまってください。"
            : "ダウンロードを中断して終了しますか？\n続ける場合は、このウィンドウをしまってください。";
        if (hasWork && !await ConfirmDialog.ShowAsync(this, "ClipDock", question, "終了する", "続ける")) return;
        shuttingDown = true;
        installCancellation?.Cancel();
        closingJobs.UnionWith(jobs.Where(j => j.CanCancel));
        foreach (var item in jobs.Where(j => j.State == "queued").ToList()) { item.State = "interrupted"; }
        var stopDownloads = scheduler.StopAsync();
        try
        {
            if (installTask is not null) await installTask;
            await stopDownloads;
        }
        catch (Exception) { /* The window closes either way; the state below is still saved. */ }
        SaveState(); allowClose = true; Close();
    }

    // Automated window check (--smoke): proves on a real desktop that the window opens, the sign-in session
    // exists, a page loads in the sign-in window and its address can be read back. Stays open briefly for a screenshot.
    private async Task RunSmokeAsync(string resultFile)
    {
        var result = new Dictionary<string, object?> { ["version"] = App.Version, ["passed"] = false };
        try
        {
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
            result["passed"] = page.Success && (string?)result["scriptUrl"] == "https://example.com/";
        }
        catch (Exception ex) { result["error"] = ex.ToString(); }
        SaveState();
        await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(result, Json.Options));
        await Task.Delay(TimeSpan.FromSeconds(12));
        allowClose = true;
        Close();
    }
}
