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
        DownloadItem Job(string path, string format = "mp4")
        {
            var item = new DownloadItem { OutputDirectory = output, Request = new DownloadRequest
            {
                Url = server + path, PageUrl = server + path, Title = "ClipDock synthetic test", Quality = "2160", Format = format
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
            var initial = new[] { Job("/a/sample.mp4"), Job("/missing/missing.mp4"), Job("/b/sample.webm"), Job("/c/sample.m3u8"), Job("/audio/sample.webm", "mp3"), Job("/dash/sample.mpd") };
            var dashProgress = new List<double>();
            initial[^1].PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DownloadItem.Percent)) dashProgress.Add(initial[^1].Percent); };
            var operations = initial.Select(Enqueue).ToArray();
            await Task.WhenAll(operations.Select(o => o.Completion)).WaitAsync(TimeSpan.FromSeconds(60));
            if (initial[1].State != "failed" || initial.Where((_, i) => i != 1).Any(j => j.State != "completed"))
                throw new Exception(string.Join("\n", initial.Select(j => j.State + ": " + j.Detail)));
            Console.WriteLine("PASS parallel MP4, MP3, WebM conversion and HLS saves continue after a 404");
            if (dashProgress.Zip(dashProgress.Skip(1)).Any(p => p.Second < p.First) || !dashProgress.Any(p => p is > 40 and < 60))
                throw new Exception("Separate streams did not keep one increasing progress bar.");
            if (Directory.EnumerateFiles(output).Any(p => Path.GetExtension(p) is not (".mp4" or ".mp3")) || Directory.Exists(DownloadPlan.WorkDirectory(output, initial[^1].Id)))
                throw new Exception("Temporary component files were left alongside completed downloads.");
            Console.WriteLine("PASS separate video/audio have one increasing progress bar and only final files are placed in the save folder");

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
            Console.WriteLine("PASS shutdown waits for all engines");

            foreach (var job in jobs.Where(j => j.IsCompleted))
            {
                var start = new ProcessStartInfo(Path.Combine(tools, "ffprobe.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name,height", "-of", "json", job.FilePath }) start.ArgumentList.Add(arg);
                using var probe = Process.Start(start)!;
                var stdout = probe.StandardOutput.ReadToEndAsync(); var stderr = probe.StandardError.ReadToEndAsync();
                await probe.WaitForExitAsync();
                using var result = JsonDocument.Parse(await stdout);
                var types = result.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToArray();
                if (probe.ExitCode != 0 || !types.Contains("audio")) throw new Exception("Invalid media: " + await stderr);
                if (job.Request.Format == "mp3")
                {
                    if (types.Contains("video") || !result.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_name").GetString() == "mp3"))
                        throw new Exception("MP3 must contain MP3 audio only.");
                }
                else if (!types.Contains("video") || result.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "video").Any(s => s.GetProperty("height").GetInt32() != 180))
                    throw new Exception("MP4 video must retain the source's 180p resolution even when 2160p is selected.");
            }
            Console.WriteLine("PASS MP3 contains audio only and 4K selection never upscales the 180p source");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { await queue.StopAsync(); }
    }
}
