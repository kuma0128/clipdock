using System.Collections.ObjectModel;
using System.IO;
using ClipDock.Core;
using Localization = ClipDock.Core.Localization;

namespace ClipDock.App;

// The download list behind both main windows (WPF on Windows, Avalonia on macOS): what may be queued or retried,
// which items the scheduler runs, how history is restored and what the summary says. It has no UI dependency,
// so these rules are checked on every OS; the windows show its messages and save its snapshot.
// Not thread-safe: the windows call it on their UI thread, and completions return to that thread.
public sealed class DownloadLibrary
{
    public const int MaxUnfinished = 100;
    public const int MaxHistory = 300;
    private readonly DownloadScheduler scheduler = new();
    private readonly Dictionary<DownloadItem, DownloadOperation> operations = [];
    // Items still unfinished at shutdown stay in the saved history even when it is full.
    private readonly HashSet<DownloadItem> closing = [];

    public ObservableCollection<DownloadItem> Jobs { get; } = [];
    public bool Stopping { get; private set; }
    // Items handed to the scheduler that have not settled yet, waiting or running.
    public int ActiveOperations => operations.Count;
    public int MaxConcurrency { get => scheduler.MaxConcurrency; set => scheduler.MaxConcurrency = value; }
    // An item changed state: the summary and the filtered list need a refresh.
    public event Action? StateChanged;
    // A scheduled download has finished, failed or been cancelled; its result is ready to save.
    public event Action<DownloadItem>? Settled;

    public bool IsScheduled(DownloadItem item) => operations.ContainsKey(item);

    // Adds a download at the top of the list. Returns null when the same download is already waiting or running.
    // Throws ArgumentException for an invalid request and InvalidOperationException when the queue is full or stopping.
    public DownloadItem? Add(DownloadRequest source, string outputDirectory)
    {
        if (Stopping) throw new InvalidOperationException(Localization.Text("終了処理中です。アプリを開き直してから保存してください。"));
        var request = source.Validated();
        request.ValidateVideoPage();
        if (IsDuplicate(request)) return null;
        if (UnfinishedCount >= MaxUnfinished) throw new InvalidOperationException(Localization.Text("待機中の動画は100件までです。"));
        var item = new DownloadItem { Request = request, Title = request.Title.Length > 0 ? request.Title : new Uri(request.Url).Host, OutputDirectory = outputDirectory };
        Attach(item);
        Jobs.Insert(0, item);
        return item;
    }

    // Queues a failed, cancelled or interrupted item again with the current login choice.
    // Retrying is a new queue insertion: the item moves to the top, behind work already waiting, and stays
    // inside the saved history window. message is what to tell the user, or null when there is nothing to say.
    public bool Retry(DownloadItem item, bool useLogin, out string? message)
    {
        message = null;
        if (Stopping || !item.CanRetry || IsScheduled(item) || !Jobs.Contains(item)) return false;
        try { item.Request.ValidateVideoPage(); }
        catch (ArgumentException ex) { message = ex.Message; return false; }
        if (IsDuplicate(item.Request)) { message = Localization.Text("この動画はすでにダウンロード一覧にあります。"); return false; }
        if (UnfinishedCount >= MaxUnfinished) { message = Localization.Text("待機中の動画は100件までです。"); return false; }
        Jobs.Move(Jobs.IndexOf(item), 0);
        item.Request = item.Request with { UseLogin = useLogin };
        item.Percent = 0; item.State = "queued"; item.Detail = Localization.Text("再試行を待っています");
        message = useLogin ? Localization.Text("最新のログイン情報を確認して、もう一度取得します。") : Localization.Text("ログイン情報を使わずに、もう一度取得します。");
        return true;
    }

    // Hands every waiting item to the scheduler, oldest first (the list is newest first). run performs one download.
    public void Start(Func<DownloadItem, CancellationToken, Task> run)
    {
        if (Stopping) return;
        foreach (var item in Jobs.Reverse().Where(j => j.State == "queued" && !IsScheduled(j)).ToList())
        {
            var operation = scheduler.Enqueue(ct => run(item, ct));
            operations.Add(item, operation);
            _ = ObserveAsync(item, operation);
        }
    }

    private async Task ObserveAsync(DownloadItem item, DownloadOperation operation)
    {
        try { await operation.Completion; }
        catch (OperationCanceledException)
        {
            // Queued work can be cancelled without invoking the download engine at all.
            item.State = Stopping ? "interrupted" : "cancelled";
            item.Detail = Stopping ? Localization.Text("終了に伴い待機を中断しました。") : Localization.Text("待機を取り消しました。");
        }
        catch (Exception ex) { item.State = "failed"; item.Detail = ex.Message; }
        finally
        {
            operations.Remove(item);
            Settled?.Invoke(item);
        }
    }

    public void Cancel(DownloadItem item)
    {
        if (operations.TryGetValue(item, out var operation)) { item.Detail = Localization.Text("キャンセルしています…"); operation.Cancel(); }
        else if (item.State == "queued") { item.State = "cancelled"; item.Detail = Localization.Text("待機を取り消しました。"); }
    }

    // Stops at shutdown: waiting items become interrupted, running ones are cancelled. Never restarts them.
    public Task StopAsync()
    {
        Stopping = true;
        closing.UnionWith(Jobs.Where(j => j.CanCancel));
        foreach (var item in Jobs.Where(j => j.State == "queued")) item.State = "interrupted";
        return scheduler.StopAsync();
    }

    // Removes finished, failed and cancelled items with their work folders. Saved files stay.
    public void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => !j.CanCancel && !IsScheduled(j)).ToList())
        {
            try { DownloadPlan.CleanWorkDirectory(job.OutputDirectory, job.Id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Jobs.Remove(job);
        }
    }

    // Restores saved history entry by entry: one unreadable record must not discard the others.
    // Nothing restarts by itself; work that was unfinished at the last exit becomes interrupted.
    public void Restore(IEnumerable<SavedDownload?> saved)
    {
        var ids = new HashSet<string>(Jobs.Select(j => j.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var item in saved.Take(MaxHistory))
        {
            if (item?.Request is null || !Guid.TryParseExact(item.Id, "N", out _) ||
                item.OutputDirectory is not { Length: > 0 } || !Path.IsPathFullyQualified(item.OutputDirectory) || !ids.Add(item.Id)) continue;
            DownloadRequest request;
            try { request = item.Request.Validated(); }
            catch (ArgumentException) { continue; }
            var terminal = item.State is "completed" or "failed" or "cancelled";
            var job = new DownloadItem
            {
                Id = item.Id, Request = request, CreatedAt = item.CreatedAt, OutputDirectory = item.OutputDirectory, Title = item.Title ?? "",
                State = terminal ? item.State : "interrupted", FilePath = item.FilePath ?? "", Percent = item.State == "completed" ? 100 : 0,
                Detail = item.State == "failed" ? DownloadFailure.Describe(request.Url, item.Detail) : terminal ? item.Detail ?? ""
                    : Localization.Text("前回の終了で中断しました。再試行するか、URLを入力し直してください。")
            };
            Attach(job);
            Jobs.Add(job);
        }
    }

    public List<SavedDownload> Snapshot() => DownloadHistory.Snapshot(Jobs, closing);

    // filter: the tag of the window's filter buttons (all, active or completed).
    public static bool Shows(DownloadItem item, string filter) => filter switch
    {
        "active" => item.CanCancel,
        "completed" => item.IsCompleted,
        _ => true
    };

    public string CountText(string filter, int shown) => Localization.Format(
        $"{(filter == "active" ? Localization.Text("ダウンロード中") : filter == "completed" ? Localization.Text("保存したファイル") : Localization.Text("ライブラリ"))}  /  {shown} 件     ·     保存完了 {Jobs.Count(j => j.IsCompleted)} 件");

    public string QueueText() => Localization.Format($"保存中 {Jobs.Count(j => j.IsBusy)} 件  ·  待機 {Jobs.Count(j => j.State == "queued")} 件");

    public bool HasUnfinished => Jobs.Any(j => j.CanCancel);
    private int UnfinishedCount => Jobs.Count(j => j.CanCancel);
    private bool IsDuplicate(DownloadRequest request) => Jobs.Any(j => j.CanCancel &&
        j.Request.Url == request.Url && j.Request.Quality == request.Quality && j.Request.Format == request.Format);

    private void Attach(DownloadItem item) => item.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName == nameof(DownloadItem.State)) StateChanged?.Invoke();
    };
}
