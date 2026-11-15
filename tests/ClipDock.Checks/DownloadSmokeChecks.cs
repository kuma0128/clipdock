using System.Diagnostics;
using System.Text.Json;
using ClipDock.App;
using ClipDock.Core;

internal static class DownloadSmokeChecks
{
    public static async Task<int> RunAsync(string tools, string server, string output)
    {
        var queue = new DownloadScheduler(2);
        var dispatchGate = new object();
        var jobs = new List<DownloadItem>();
        void Dispatch(Action action) { lock (dispatchGate) action(); }
        DownloadItem Job(string path)
        {
            var item = new DownloadItem { OutputDirectory = output, Request = new DownloadRequest
            {
                Url = server + path, PageUrl = server + path, Title = "ClipDock synthetic test", Quality = "720",
                Cookies = [new BrowserCookie { Domain = new Uri(server).Host, HostOnly = true, Name = "clipdock_test", Value = "synthetic-only" }]
            } };
            jobs.Add(item);
            return item;
        }
        DownloadOperation Enqueue(DownloadItem item) => queue.Enqueue(ct => DownloadEngine.RunAsync(item, tools, Dispatch, ct));
        async Task Until(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                lock (dispatchGate) { if (condition()) return; }
                await Task.Delay(25, deadline.Token);
            }
        }
        try
        {
            if (!ToolInstaller.IsReady(tools)) throw new IOException("Provide a complete tools directory.");
            var initial = new[] { Job("/a/sample.mp4"), Job("/missing/missing.mp4"), Job("/b/sample.webm"), Job("/c/sample.m3u8") };
            var operations = initial.Select(Enqueue).ToArray();
            await Task.WhenAll(operations.Select(o => o.Completion)).WaitAsync(TimeSpan.FromSeconds(60));
            if (initial[1].State != "failed" || initial.Where((_, i) => i != 1).Any(j => j.State != "completed"))
                throw new Exception(string.Join("\n", initial.Select(j => j.State + ": " + j.Detail)));
            Console.WriteLine("PASS parallel MP4, WebM conversion and HLS saves continue after a 404");

            var cancelled = Job("/slow-cancel/sample.mp4"); var other = Job("/d/sample.mp4");
            var cancelledOperation = Enqueue(cancelled); var otherOperation = Enqueue(other);
            await Until(() => cancelled.Percent > 0 && cancelled.State == "downloading");
            cancelledOperation.Cancel(); await cancelledOperation.Completion; await otherOperation.Completion;
            if (cancelled.State != "cancelled" || other.State != "completed") throw new Exception("Cancellation affected another save.");
            cancelled.State = "queued";
            await Enqueue(cancelled).Completion.WaitAsync(TimeSpan.FromSeconds(40));
            if (cancelled.State != "completed") throw new Exception("Retry failed: " + cancelled.Detail);
            Console.WriteLine("PASS cancellation is isolated and the same job can resume");

            var first = Job("/slow-stop1/sample.mp4"); var second = Job("/slow-stop2/sample.mp4"); var pending = Job("/queued/sample.mp4");
            var firstOperation = Enqueue(first); var secondOperation = Enqueue(second); var pendingOperation = Enqueue(pending);
            await Until(() => first.Percent > 0 && second.Percent > 0);
            await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (first.State != "cancelled" || second.State != "cancelled" || !pendingOperation.Completion.IsCanceled)
                throw new Exception("Shutdown failed to cancel all operations.");
            foreach (var job in jobs)
                if (File.Exists(Path.Combine(AppPaths.Secrets, job.Id + ".cookies"))) throw new Exception("A test cookie file remains.");
            Console.WriteLine("PASS shutdown waits for all engines and leaves no test cookies");

            foreach (var job in jobs.Where(j => j.IsCompleted))
            {
                var start = new ProcessStartInfo(Path.Combine(tools, "ffprobe.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type", "-of", "json", job.FilePath }) start.ArgumentList.Add(arg);
                using var probe = Process.Start(start)!;
                var stdout = probe.StandardOutput.ReadToEndAsync(); var stderr = probe.StandardError.ReadToEndAsync();
                await probe.WaitForExitAsync();
                using var result = JsonDocument.Parse(await stdout);
                var types = result.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToArray();
                if (probe.ExitCode != 0 || !types.Contains("video") || !types.Contains("audio")) throw new Exception("Invalid MP4: " + await stderr);
            }
            Console.WriteLine("PASS every completed MP4 contains video and audio");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { await queue.StopAsync(); }
    }
}
