using ClipDock.App;
using ClipDock.Core;
using static CheckAssert;

// DownloadEngine against a scripted stand-in for yt-dlp: the output parsing, completion checks, error redaction and
// cancellation run in a second instead of a real download. The real engine is exercised by smoke-downloads.mjs
// (Windows) and scripts/test-mac.sh (macOS).
internal static class EngineChecks
{
    // Behaves according to the last path segment of the URL. It reads the same arguments DownloadPlan passes to yt-dlp.
    private const string StandIn = """
        #!/bin/sh
        out=""; cookies=""; prev=""; url=""
        for arg in "$@"; do
          if [ "$prev" = "--paths" ] && [ -z "$out" ]; then out="$arg"; fi
          if [ "$prev" = "--cookies" ]; then cookies="$arg"; fi
          prev="$arg"; url="$arg"
        done
        case "$url" in
          */ok)
            echo 'CLIPDOCK_TITLE "Stand-in title"'
            echo 'CLIPDOCK_PARTS ["137","140"]'
            echo 'CLIPDOCK_PROGRESS {"downloaded_bytes":50,"total_bytes":100,"speed":1048576,"eta":3,"status":"downloading","filename":"video"}' >&2
            echo 'CLIPDOCK_PROGRESS {"downloaded_bytes":100,"total_bytes":100,"status":"finished","filename":"video"}' >&2
            echo 'CLIPDOCK_PROGRESS {"downloaded_bytes":20,"total_bytes":100,"status":"downloading","filename":"audio"}' >&2
            echo 'CLIPDOCK_PROGRESS {"downloaded_bytes":100,"total_bytes":100,"status":"finished","filename":"audio"}' >&2
            file="$out/Stand-in [id]-0000.mp4"; printf video > "$file"
            printf 'CLIPDOCK_FILE "%s"\n' "$file" ;;
          */outside)
            printf 'CLIPDOCK_FILE "%s"\n' "/tmp/elsewhere.mp4" ;;
          */slow)
            exec sleep 30 ;;
          *)
            i=0
            while [ $i -lt 8 ]; do echo "ERROR: part $i https://cdn.example/v$i.mp4?token=secret$i" >&2; i=$((i+1)); done
            if [ -n "$cookies" ]; then echo "ERROR: rejected $(tail -n 1 "$cookies" | cut -f 7) from $cookies" >&2; fi
            exit 1 ;;
        esac
        """;

    public static void Run(Action<string, Action> check)
    {
        void Async(string name, Func<Fixture, Task> test) => check(name, () =>
        {
            if (OperatingSystem.IsWindows()) throw new SkipCheckException("the stand-in engine is a shell script; Windows runs the real engine in smoke-downloads.mjs.");
            using var fixture = new Fixture();
            test(fixture).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        });

        Async("a save reports one rising progress bar, the title and a verified file, then removes its work folder", async fixture =>
        {
            var (item, percents, states) = await fixture.RunAsync("ok");
            Assert(item.State == "completed" && item.Percent == 100 && item.Title == "Stand-in title", $"{item.State}: {item.Detail}");
            Assert(File.Exists(item.FilePath) && DownloadPlan.IsOutputFile(item.FilePath, fixture.Output) && item.Detail == Path.GetFileName(item.FilePath));
            Assert(percents.SequenceEqual(percents.Order()) && percents.Take(percents.Count - 1).All(p => p <= 95), string.Join(", ", percents));
            Assert(states.Contains("converting") && !Directory.Exists(DownloadPlan.WorkDirectory(fixture.Output, item.Id)));
        });

        Async("an engine error keeps its last lines without signed URL queries, login values or the session path", async fixture =>
        {
            SessionCookieFile? session = null;
            var (item, _, _) = await fixture.RunAsync("fail", (request, _) =>
            {
                session = SessionCookieFile.Create(request.Url, [new SessionCookie(".example.com", "/", "sid", "private-login-value", true, true, 0)],
                    Path.Combine(fixture.Root, "sessions"));
                return Task.FromResult(session);
            });
            var sessionFile = session?.FilePath ?? throw new Exception("No session file was requested.");
            var lines = item.Detail.Split('\n');
            Assert(item.State == "failed" && lines.Length == 6, item.Detail);
            Assert(!item.Detail.Contains("token=") && item.Detail.Contains("https://cdn.example/v7.mp4"), item.Detail);
            Assert(!item.Detail.Contains("private-login-value") && !item.Detail.Contains(sessionFile), item.Detail);
            Assert(lines[^1] == "ERROR: rejected [login] from [session]", lines[^1]);
            Assert(!File.Exists(sessionFile), "the temporary cookie file is deleted after the save");
        });

        Async("a reported file outside the destination is not accepted as the saved video", async fixture =>
        {
            var (item, _, _) = await fixture.RunAsync("outside");
            Assert(item.State == "failed" && item.FilePath == "" && item.Detail.Contains("動画そのもののページのURL"), item.Detail);
        });

        Async("cancelling a running save stops the engine promptly and offers to resume", async fixture =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (item, _, _) = await fixture.RunAsync("slow", ct: cancellation.Token);
            Assert(item.State == "cancelled" && clock.Elapsed < TimeSpan.FromSeconds(10), $"{item.State} after {clock.Elapsed}");
        });
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ClipDock-engine-check-" + Guid.NewGuid().ToString("N"));
        public string Tools => Path.Combine(Root, "tools");
        public string Output => Path.Combine(Root, "保存先 with spaces");

        public Fixture()
        {
            Directory.CreateDirectory(Tools);
            File.WriteAllText(Path.Combine(Tools, "yt-dlp"), StandIn.ReplaceLineEndings("\n") + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(Tools, "yt-dlp"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var name in new[] { "ffmpeg", "ffprobe", "deno" }) File.WriteAllText(Path.Combine(Tools, name), "");
        }

        public async Task<(DownloadItem Item, List<double> Percents, List<string> States)> RunAsync(string page,
            Func<DownloadRequest, CancellationToken, Task<SessionCookieFile?>>? sessions = null, CancellationToken ct = default)
        {
            var item = new DownloadItem { OutputDirectory = Output, Request = new DownloadRequest { Url = "https://example.com/" + page }.Validated() };
            var gate = new object();
            var percents = new List<double>();
            var states = new List<string>();
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DownloadItem.Percent)) percents.Add(item.Percent);
                if (args.PropertyName == nameof(DownloadItem.State)) states.Add(item.State);
            };
            await DownloadEngine.RunAsync(item, Tools, action => { lock (gate) action(); }, ct, sessions);
            return (item, percents, states);
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
