using System.Collections.Concurrent;
using ClipDock.App;
using ClipDock.Core;
using static CheckAssert;

// The download list shared by the Windows and macOS windows, without either UI.
internal static class LibraryChecks
{
    private static DownloadRequest Request(string url, string quality = "1080", string format = "mp4") => new() { Url = url, Quality = quality, Format = format };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Run(Action<string, Action> check)
    {
        var output = Rooted("videos");

        check("the list rejects duplicates of unfinished downloads and caps waiting work", () =>
        {
            var library = new DownloadLibrary();
            var first = library.Add(Request("https://example.com/a"), output)!;
            Assert(library.Jobs[0] == first && first.Title == "example.com" && first.OutputDirectory == output && first.State == "queued");
            Assert(library.Add(Request("https://example.com/a"), output) is null);
            Assert(library.Add(Request("https://example.com/a", quality: "720"), output) is not null);
            Assert(library.Add(Request("https://example.com/a", format: "mp3"), output) is not null);
            first.State = "completed";
            Assert(library.Add(Request("https://example.com/a"), output) is not null, "a finished download can be saved again");
            Throws<ArgumentException>(() => library.Add(Request("ftp://example.com/a"), output));
            Throws<ArgumentException>(() => library.Add(Request("https://www.youtube.com/"), output));
            Throws<ArgumentException>(() => library.Add(Request("https://example.com/a", quality: "999"), output));
            while (library.Jobs.Count(j => j.CanCancel) < DownloadLibrary.MaxUnfinished)
                library.Add(Request("https://example.com/v" + library.Jobs.Count), output);
            Throws<InvalidOperationException>(() => library.Add(Request("https://example.com/over"), output));
            Assert(library.Jobs[0].Request.Url != "https://example.com/over");
        });

        check("retry moves the item to the top with the current login choice and refuses duplicates", () =>
        {
            var library = new DownloadLibrary();
            var failed = library.Add(Request("https://example.com/a"), output)!;
            var newer = library.Add(Request("https://example.com/b"), output)!;
            Assert(!library.Retry(newer, true, out var message) && message is null, "a waiting item is not retried");
            failed.State = "failed"; failed.Percent = 40;
            Assert(library.Retry(failed, false, out message));
            Assert(library.Jobs[0] == failed && failed.State == "queued" && failed.Percent == 0 && !failed.Request.UseLogin);
            Assert(message == Localization.Text("ログイン情報を使わずに、もう一度取得します。"));
            failed.State = "cancelled";
            library.Add(Request("https://example.com/a"), output);
            Assert(!library.Retry(failed, true, out message) && message == Localization.Text("この動画はすでにダウンロード一覧にあります。"));
            Assert(failed.State == "cancelled");
        });

        check("history restore skips unreadable records and never restarts unfinished work", () =>
        {
            SavedDownload Saved(string state, string url = "https://example.com/v", string? id = null, string? directory = null, string detail = "") =>
                new(id ?? Guid.NewGuid().ToString("N"), Request(url), DateTimeOffset.Now, directory ?? output, "title", state, "", detail);
            var duplicate = Saved("completed");
            var library = new DownloadLibrary();
            library.Restore([
                null, Saved("completed", id: "not-a-guid"), Saved("completed", directory: "relative"), Saved("completed", url: "javascript:alert(1)"),
                duplicate, duplicate with { Title = "second copy" },
                Saved("downloading"), Saved("queued"),
                Saved("failed", url: "https://x.com/a/status/1", detail: "ERROR: No video could be found in this tweet"),
                .. Enumerable.Range(0, 400).Select(_ => Saved("cancelled"))
            ]);
            Assert(library.Jobs.Count == DownloadLibrary.MaxHistory - 5, "the first 300 records are read; five of them are unusable");
            Assert(library.Jobs[0].Id == duplicate.Id && library.Jobs[0].Percent == 100 && library.Jobs.Count(j => j.Id == duplicate.Id) == 1);
            Assert(library.Jobs[1].State == "interrupted" && library.Jobs[2].State == "interrupted" && !library.HasUnfinished);
            Assert(library.Jobs[3].Detail.StartsWith("Xの動画情報を取得できませんでした。", StringComparison.Ordinal));
        });

        check("the summary counts saved files and the tooltip counts work in progress", () =>
        {
            var library = new DownloadLibrary();
            library.Add(Request("https://example.com/a"), output);
            var busy = library.Add(Request("https://example.com/b"), output)!;
            var done = library.Add(Request("https://example.com/c"), output)!;
            var failed = library.Add(Request("https://example.com/d"), output)!;
            busy.State = "converting"; done.State = "completed"; failed.State = "failed";
            Assert(library.CompletedCount == 1);
            Assert(library.QueueText() == "保存中 1 件  ·  待機 1 件");
        });

        check("only a failed, cancelled or interrupted item can be removed, with its work folder; saved files stay", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ClipDock-library-" + Guid.NewGuid().ToString("N"));
            try
            {
                var library = new DownloadLibrary();
                var done = library.Add(Request("https://example.com/a"), root)!;
                var waiting = library.Add(Request("https://example.com/b"), root)!;
                var failed = library.Add(Request("https://example.com/c"), root)!;
                done.State = "completed"; failed.State = "failed";
                foreach (var item in library.Jobs) DownloadPlan.PrepareWorkDirectory(root, item.Id);
                var saved = Path.Combine(root, "video.mp4");
                File.WriteAllText(saved, "video");
                Assert(!library.Remove(done) && !library.Remove(waiting) && library.Jobs.Count == 3);
                Assert(library.Remove(failed) && !library.Remove(failed));
                Assert(library.Jobs.SequenceEqual([waiting, done]) && File.Exists(saved));
                Assert(!Directory.Exists(DownloadPlan.WorkDirectory(root, failed.Id)) && Directory.Exists(DownloadPlan.WorkDirectory(root, waiting.Id)));
            }
            finally { Directory.Delete(root, true); }
        });

        check("scheduled downloads start oldest first, settle once each, and cancel or interrupt as the window expects", () => UiThread.Run(async () =>
        {
            var library = new DownloadLibrary { MaxConcurrency = 1 };
            var oldest = library.Add(Request("https://example.com/a"), output)!;
            var middle = library.Add(Request("https://example.com/b"), output)!;
            var newest = library.Add(Request("https://example.com/c"), output)!;
            var started = new ConcurrentDictionary<DownloadItem, TaskCompletionSource>();
            var release = new ConcurrentDictionary<DownloadItem, TaskCompletionSource>();
            var settled = new Dictionary<DownloadItem, TaskCompletionSource>();
            foreach (var item in library.Jobs) { started[item] = Signal(); release[item] = Signal(); settled[item] = Signal(); }
            var settledCount = 0;
            library.Settled += item => { settledCount++; settled[item].SetResult(); };
            var changes = 0;
            library.StateChanged += () => changes++;

            library.Start(async (item, ct) =>
            {
                started[item].SetResult();
                await release[item].Task.WaitAsync(ct);
                if (item.Request.Url.EndsWith("/a", StringComparison.Ordinal)) throw new IOException("synthetic engine failure");
            });
            library.Start((_, _) => throw new Exception("Already scheduled items must not run twice."));
            Assert(library.ActiveOperations == 3 && library.IsScheduled(newest));
            await started[oldest].Task;
            Assert(!started[middle].Task.IsCompleted && !started[newest].Task.IsCompleted);

            library.Cancel(newest);
            await settled[newest].Task;
            Assert(newest.State == "cancelled" && newest.Detail == Localization.Text("待機を取り消しました。") && !started[newest].Task.IsCompleted);

            release[oldest].SetResult();
            await settled[oldest].Task;
            Assert(oldest.State == "failed" && oldest.Detail == "synthetic engine failure");
            await started[middle].Task;

            var stop = library.StopAsync();
            await stop; await settled[middle].Task;
            Assert(middle.State == "interrupted" && library.ActiveOperations == 0 && settledCount == 3 && changes >= 3);
            Assert(library.Snapshot().Any(s => s.Id == middle.Id));
            Throws<InvalidOperationException>(() => library.Add(Request("https://example.com/d"), output));
            Assert(!library.Retry(middle, true, out _), "nothing is queued again while the app is closing");
        }));
    }
}

// Runs continuations one at a time on the calling thread, as a window's dispatcher does.
internal sealed class UiThread : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
    public override void Post(SendOrPostCallback d, object? state)
    {
        // Work posted after the check has finished still runs, just no longer in order.
        try { queue.Add((d, state)); }
        catch (InvalidOperationException) { ThreadPool.QueueUserWorkItem(_ => d(state)); }
    }
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public static void Run(Func<Task> test)
    {
        var context = new UiThread();
        var previous = Current;
        SetSynchronizationContext(context);
        try
        {
            var task = test().WaitAsync(TimeSpan.FromSeconds(15));
            task.ContinueWith(_ => context.queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context.queue.GetConsumingEnumerable()) callback(state);
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}
