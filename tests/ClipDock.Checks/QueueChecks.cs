using System.Collections.Concurrent;
using System.Text.Json;
using ClipDock.App;
using ClipDock.Core;

internal static class QueueChecks
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Assert(bool condition) { if (!condition) throw new Exception("Queue assertion failed"); }
    private static async Task Cancelled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation");
    }

    public static void Run(Action<string, Action> check)
    {
        void Async(string name, Func<Task> test) => check(name, () => test().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult());

        Async("parallel queue fills only the configured slots and starts waiting work FIFO", async () =>
        {
            var queue = new DownloadScheduler(2);
            var started = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
            var finish = Enumerable.Range(0, 4).Select(_ => Signal()).ToArray();
            var running = 0;
            var counts = new ConcurrentBag<int>();
            var operations = Enumerable.Range(0, 4).Select(i => queue.Enqueue(async ct =>
            {
                counts.Add(Interlocked.Increment(ref running)); started[i].SetResult();
                try { await finish[i].Task.WaitAsync(ct); }
                finally { Interlocked.Decrement(ref running); }
            })).ToArray();
            try
            {
                await Task.WhenAll(started[0].Task, started[1].Task);
                Assert(!started[2].Task.IsCompleted && !started[3].Task.IsCompleted);
                finish[0].SetResult(); await started[2].Task;
                Assert(!started[3].Task.IsCompleted);
                finish[1].SetResult(); await started[3].Task;
                Assert(counts.Max() == 2);
                finish[2].SetResult(); finish[3].SetResult();
                await Task.WhenAll(operations.Select(o => o.Completion));
            }
            finally { await queue.StopAsync(); }
        });

        Async("changing concurrency fills new slots and lowering it never interrupts active saves", async () =>
        {
            var queue = new DownloadScheduler(1);
            var started = Enumerable.Range(0, 3).Select(_ => Signal()).ToArray();
            var finish = Enumerable.Range(0, 3).Select(_ => Signal()).ToArray();
            var operations = Enumerable.Range(0, 3).Select(i => queue.Enqueue(async ct =>
            {
                started[i].SetResult(); await finish[i].Task.WaitAsync(ct);
            })).ToArray();
            try
            {
                await started[0].Task; queue.MaxConcurrency = 2; await started[1].Task;
                queue.MaxConcurrency = 1; finish[0].SetResult(); await operations[0].Completion;
                Assert(!started[2].Task.IsCompleted && !operations[1].Completion.IsCompleted);
                finish[1].SetResult(); await started[2].Task;
                finish[2].SetResult(); await operations[2].Completion;
            }
            finally { await queue.StopAsync(); }
        });

        Async("cancelling queued work never starts its process and does not block the next save", async () =>
        {
            var queue = new DownloadScheduler(1);
            var started = Signal(); var release = Signal(); var invoked = false;
            try
            {
                var first = queue.Enqueue(async ct => { started.SetResult(); await release.Task.WaitAsync(ct); });
                await started.Task;
                var removed = queue.Enqueue(_ => { invoked = true; return Task.CompletedTask; });
                var next = queue.Enqueue(_ => Task.CompletedTask);
                removed.Cancel(); await Cancelled(removed.Completion);
                release.SetResult(); await next.Completion; await first.Completion;
                Assert(!invoked);
            }
            finally { await queue.StopAsync(); }
        });

        Async("cancelling one running save frees its slot without cancelling another", async () =>
        {
            var queue = new DownloadScheduler(2);
            var firstStarted = Signal(); var secondStarted = Signal(); var release = Signal();
            try
            {
                var first = queue.Enqueue(async ct => { firstStarted.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
                var second = queue.Enqueue(async ct => { secondStarted.SetResult(); await release.Task.WaitAsync(ct); });
                var next = queue.Enqueue(_ => Task.CompletedTask);
                await Task.WhenAll(firstStarted.Task, secondStarted.Task);
                first.Cancel(); await Cancelled(first.Completion); await next.Completion;
                Assert(!second.Completion.IsCompleted);
                release.SetResult(); await second.Completion;
            }
            finally { await queue.StopAsync(); }
        });

        Async("an engine failure releases its slot and synchronous completions do not strand work", async () =>
        {
            var queue = new DownloadScheduler(2);
            try
            {
                var failed = queue.Enqueue(_ => throw new IOException("synthetic failure"));
                var following = Enumerable.Range(0, 50).Select(_ => queue.Enqueue(_ => Task.CompletedTask)).ToArray();
                try { await failed.Completion; throw new Exception("Expected failure"); } catch (IOException) { }
                await Task.WhenAll(following.Select(o => o.Completion));
            }
            finally { await queue.StopAsync(); }
        });

        Async("shutdown cancels queued and active saves and awaits all cleanup", async () =>
        {
            var queue = new DownloadScheduler(2);
            var started = new[] { Signal(), Signal() }; var cleaning = new[] { Signal(), Signal() };
            var releaseCleanup = Signal(); var queuedInvoked = false;
            try
            {
                var operations = Enumerable.Range(0, 2).Select(i => queue.Enqueue(async ct =>
                {
                    started[i].SetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    finally { cleaning[i].SetResult(); await releaseCleanup.Task; }
                })).ToArray();
                var queued = queue.Enqueue(_ => { queuedInvoked = true; return Task.CompletedTask; });
                await Task.WhenAll(started.Select(s => s.Task));
                var stopped = queue.StopAsync();
                await Task.WhenAll(cleaning.Select(s => s.Task));
                Assert(!stopped.IsCompleted && !queuedInvoked);
                await Cancelled(queued.Completion);
                releaseCleanup.SetResult(); await stopped;
                Assert(operations.All(o => o.Completion.IsCompleted));
                try { queue.Enqueue(_ => Task.CompletedTask); throw new Exception("Queue accepted work after shutdown"); }
                catch (InvalidOperationException) { }
            }
            finally { releaseCleanup.TrySetResult(); await queue.StopAsync(); }
        });

        Async("cancellation before engine preparation does not create output files or retain cookies", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var output = Path.Combine(Path.GetTempPath(), "ClipDock-not-created-" + Guid.NewGuid().ToString("N"));
            var job = new DownloadItem { OutputDirectory = output, Request = new DownloadRequest { Url = "https://example.com/video",
                Cookies = [new BrowserCookie { Domain = "example.com", Name = "test", Value = "synthetic" }] } };
            await DownloadEngine.RunAsync(job, output, action => action(), cancellation.Token);
            Assert(job.State == "cancelled" && job.Request.Cookies.Count == 0 && !Directory.Exists(output));
        });

        check("old settings default to two saves and the selected concurrency survives serialization", () =>
        {
            var legacy = JsonSerializer.Deserialize<AppSettings>("{\"outputDirectory\":\"C:/videos\",\"quality\":\"720\"}", Json.Options)!;
            Assert(legacy.MaxConcurrentDownloads == 2 && legacy.Quality == "720");
            var saved = legacy with { MaxConcurrentDownloads = 4 };
            Assert(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved, Json.Options), Json.Options) == saved);
        });

        check("history keeps old active saves and shutdown entries within its 300 item limit", () =>
        {
            var jobs = Enumerable.Range(0, 350).Select(_ => new DownloadItem { State = "completed" }).ToList();
            var active = new DownloadItem { State = "downloading", Request = new DownloadRequest { Cookies = [new BrowserCookie { Value = "secret" }] } };
            jobs.Add(active);
            var snapshot = DownloadHistory.Snapshot(jobs);
            Assert(snapshot.Count == 300 && snapshot[^1].Id == active.Id && snapshot[^1].Request.Cookies.Count == 0);
            active.State = "interrupted";
            Assert(DownloadHistory.Snapshot(jobs, [active])[^1].Id == active.Id);
        });
    }
}
