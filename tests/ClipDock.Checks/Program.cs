

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClipDock.App;
using ClipDock.Core;
using static CheckAssert;

if (args is ["--download-smoke", var tools, var server, var output])
    return await DownloadSmokeChecks.RunAsync(tools, server, output);
// --filter <text>: run only the checks whose name contains the text (case-insensitive).
var filter = args is ["--filter", var text] ? text : null;
var passed = 0;
var failed = 0;
var skipped = 0;
var clock = Stopwatch.StartNew();
var notes = new List<string>();
void Check(string name, Action check)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    try { check(); passed++; Console.WriteLine("PASS " + name); }
    catch (SkipCheckException ex) { skipped++; Console.WriteLine("SKIP " + name + ": " + ex.Message); notes.Add($"- SKIP {name}: {ex.Message}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); notes.Insert(0, $"- **FAIL** {name}: `{ex.Message.ReplaceLineEndings(" ").Replace('`', '\'')}`"); }
}

Check("language settings survive restart and legacy or invalid preferences fall back to Japanese", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "clipdock-language-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, "settings.json");
    try
    {
        Assert(Localization.ReadPreference(file) == "ja");
        foreach (var language in Localization.Languages)
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new AppSettings("videos", "1080", Language: language), Json.Options));
            Assert(Localization.ReadPreference(file) == language);
            Assert(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json.Options)!.Language == language);
        }
        foreach (var json in new[] { "{}", "{\"language\":\"unknown\"}", "{\"language\":null}", "{\"language\":42}", "null", "broken" })
        { File.WriteAllText(file, json); Assert(Localization.ReadPreference(file) == "ja"); }
        Assert(JsonSerializer.Deserialize<AppSettings>("{\"outputDirectory\":\"videos\",\"quality\":\"720\"}", Json.Options)!.Language == "ja");
        Assert(Localization.Normalize("zh-cn") == "zh-CN");
    }
    finally { Directory.Delete(directory, true); }
});
Check("translations retain all format arguments and every XAML resource has a value", () =>
{
    using var stream = typeof(Localization).Assembly.GetManifestResourceStream("ClipDock.Core.Localization.strings.json")!;
    var catalog = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    string[] Arguments(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\{[0-9]+[^}]*\}")
        .Select(m => m.Value).Order().ToArray();
    foreach (var (source, translations) in catalog)
    {
        Assert(translations.Length == 3);
        foreach (var text in translations)
        {
            Assert(!string.IsNullOrWhiteSpace(text));
            Assert(CompositeFormat.Parse(source).MinimumArgumentCount == CompositeFormat.Parse(text).MinimumArgumentCount);
            Assert(Arguments(source).SequenceEqual(Arguments(text)));
        }
    }
    try
    {
        foreach (var language in Localization.Languages)
        {
            Localization.Initialize(language);
            var resources = Localization.Resources.ToDictionary();
            Assert(resources.Count == catalog.Count && resources.Values.All(v => v.Length > 0));
            var item = new DownloadItem { Request = new DownloadRequest { Format = "mp3" }, State = "converting", Title = "日本語の動画" };
            Assert(item.StateLabel.Contains("MP3") && item.Title == "日本語の動画" && item.State == "converting");
            Assert(Localization.Format($"  ·  残り約 {TimeSpan.FromSeconds(90):hh\\:mm\\:ss}").Contains("00:01:30"));
            if (language != "ja") Assert(!item.StateLabel.Contains("仕上げ"));
        }
        Localization.Initialize("en");
        Assert(Localization.Text("保存先") == "Destination");
        Assert(Localization.Text("external error") == "external error");
        Assert(Localization.Format($"保存中 {2} 件  ·  待機 {3} 件") == "2 active  ·  3 queued");
        Assert(Localization.Text("保存先", "es") == "Destino" && Localization.Language == "en");
    }
    finally { Localization.Initialize("ja"); }
});

Check("sign-in startup points at this copy, follows a move and respects Windows' Startup apps switch", () =>
{
    if (!OperatingSystem.IsWindows()) throw new SkipCheckException("the Run key exists only on Windows.");
    CheckStartup();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void CheckStartup()
    {
        var root = @"Software\ClipDock-check-" + Guid.NewGuid().ToString("N");
        var startup = new StartupRegistration(root + @"\Run", root + @"\StartupApproved");
        var installed = @"C:\Users\Test User\AppData\Local\Programs\ClipDock\ClipDock.exe";
        var moved = @"D:\Portable ClipDock\ClipDock.exe";
        try
        {
            Assert(!startup.IsEnabled(installed) && !startup.DisabledByWindows());
            startup.Enable(installed, clearWindowsSwitch: false);
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\Run")!)
                Assert((string)run.GetValue(StartupRegistration.ValueName)! == "\"" + installed + "\" --background");
            Assert(startup.IsEnabled(installed) && startup.Targets(installed.ToUpperInvariant()) && !startup.Targets(moved));
            startup.Enable(moved, clearWindowsSwitch: false);
            Assert(startup.IsEnabled(moved) && !startup.Targets(installed));

            // Turned off in Settings > Apps > Startup: launch keeps it off, the app's own switch turns it back on.
            using (var approval = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\StartupApproved"))
                approval.SetValue(StartupRegistration.ValueName, new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, Microsoft.Win32.RegistryValueKind.Binary);
            startup.Enable(moved, clearWindowsSwitch: false);
            Assert(startup.DisabledByWindows() && !startup.IsEnabled(moved) && startup.Targets(moved));
            startup.Enable(moved, clearWindowsSwitch: true);
            Assert(!startup.DisabledByWindows() && startup.IsEnabled(moved));
            using (var approval = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\StartupApproved"))
                approval.SetValue(StartupRegistration.ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Microsoft.Win32.RegistryValueKind.Binary);
            Assert(!startup.DisabledByWindows() && startup.IsEnabled(moved));

            startup.Disable();
            Assert(!startup.Targets(moved));
            startup.Disable();
            foreach (var command in new string?[] { null, "", "\"", "\"\"", "C:\\ClipDock.exe", "\"C:\\ClipDock.exe", "\"\0\" --background" })
                Assert(StartupRegistration.Target(command) is null);
            Assert(StartupRegistration.Target(StartupRegistration.Command(moved)) == moved);
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }
});
Check("only absolute HTTP(S) URLs are accepted", () =>
{
    foreach (var url in new[] { "file:///C:/secret", "javascript:alert(1)", "ftp://a.com/file", "--exec=calc", "https://u:p@example.com/v", "https://example.com/\n--exec", "", "https://" })
        Assert(!DownloadRequest.IsWebUrl(url));
    Assert(DownloadRequest.IsWebUrl("https://example.com/video?id=a&token=b"));
});
Check("unsupported qualities and formats cannot become downloader arguments", () =>
{

    Throws<ArgumentException>(() => new DownloadRequest { Url = "https://example.com", Quality = "1080 --exec calc" }.Validated());
    foreach (var format in new string?[] { "avi", "MP3", "mp3 --exec calc", "", null })
        Throws<ArgumentException>(() => new DownloadRequest { Url = "https://example.com", Format = format! }.Validated());
});
Check("legacy requests, history and settings default to MP4 with browser overlay off", () =>
{
    var request = JsonSerializer.Deserialize<DownloadRequest>("{\"url\":\"https://example.com/video\"}", Json.Options)!.Validated();
    var settings = JsonSerializer.Deserialize<AppSettings>("{\"outputDirectory\":\"videos\",\"quality\":\"720\"}", Json.Options)!;
    Assert(request.Format == "mp4" && settings.Format == "mp4" && !settings.BrowserOverlayEnabled && request.UseLogin && settings.UseLogin);
    // Settings written before the notification-area version start at sign-in and explain the icon once.
    Assert(settings.StartAtLogin && !settings.TrayNoticeShown);
    var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings with { StartAtLogin = false, TrayNoticeShown = true }, Json.Options), Json.Options)!;
    Assert(!saved.StartAtLogin && saved.TrayNoticeShown);
    var item = new DownloadItem { Request = request with { Format = "mp3" }, State = "converting" };
    Assert(item.FormatLabel == "MP3" && item.StateLabel.Contains("MP3") && item.Meta.Contains("音声のみ"));
    Assert(item.Save().Request.Format == "mp3");
});
Check("legacy history keeps download fields and drops retired browser credentials", () =>
{
    var request = JsonSerializer.Deserialize<DownloadRequest>("{\"url\":\"https://example.com/video\",\"quality\":\"720\",\"cookies\":[{\"value\":\"old-secret\"}],\"restrictYouTube\":true}", Json.Options)!.Validated();
    var saved = JsonSerializer.Serialize(new DownloadItem { Request = request }.Save(), Json.Options);
    Assert(request.Quality == "720" && request.Format == "mp4" && !saved.Contains("old-secret") && !saved.Contains("cookies") && !saved.Contains("restrictYouTube"));
});
Check("X missing-video errors explain authentication limits and preserve diagnostics", () =>
{
    const string error = "[twitter] Downloading GraphQL JSON\nERROR: [twitter] 123: No video could be found in this tweet\nURL: https://x.com/example/status/123 [Cookie]";
    foreach (var url in new[] { "https://x.com/example/status/123", "https://twitter.com/example/status/123", "https://mobile.twitter.com/example/status/123", "https://WWW.X.COM/example/status/123" })
    {
        var detail = DownloadFailure.Describe(url, error);
        Assert(detail.StartsWith("Xの動画情報を取得できませんでした。", StringComparison.Ordinal));
        Assert(detail.Contains("場合があります。") && detail.Contains("ClipDock内でXにログイン") && detail.Contains("ONにして再試行"));
        Assert(detail.EndsWith("\n" + error, StringComparison.Ordinal));
        Assert(DownloadFailure.Describe(url, detail) == detail);
    }
});
Check("X failure guidance leaves other errors and unrelated or malformed URLs unchanged", () =>
{
    const string error = "ERROR: No video could be found in this tweet";
    foreach (var url in new string?[] { "https://example.com/video", "https://x.com.example.com/video", "https://nottwitter.com/video", "file://x.com/video", "not a URL", "", null })
        Assert(DownloadFailure.Describe(url, error) == error);
    foreach (var detail in new[] { "HTTP Error 403: Forbidden", "一時ログイン情報を削除できませんでした。", "", "ERROR: Requested tweet is unavailable" })
        Assert(DownloadFailure.Describe("https://x.com/example/status/123", detail) == detail);
    Assert(DownloadFailure.Describe("https://x.com/example/status/123", null) == "");
});
Check("invalid referer and header controls are removed", () =>
{
    var r = new DownloadRequest { Url = "https://example.com/v", Referer = "https://evil.test/\r\nX: y", UserAgent = "Chrome\r\nBad", Title = new string('a', 500) }.Validated();
    Assert(r.Referer == "" && r.UserAgent == "ChromeBad" && r.Title.Length == 200);
});
Check("text limits never keep half of a surrogate pair", () =>
{
    Assert(DownloadRequest.CleanText("ab🎬", 3) == "ab");
    Assert(DownloadRequest.CleanText("ab🎬", 4) == "ab🎬");
    Assert(DownloadRequest.CleanText(null, 4) == "" && DownloadRequest.CleanText("a\tb", 0) == "");
});
Check("progress parses download percentages, speed and ETA", () =>
{
    var p = DownloadProgress.Parse("CLIPDOCK_PROGRESS {\"downloaded_bytes\":512,\"total_bytes\":1024,\"speed\":128,\"eta\":4,\"status\":\"downloading\"}");
    Assert(p is { Percent: 50, BytesPerSecond: 128, SecondsRemaining: 4, Finished: false });
});
Check("unknown sizes and malformed progress do not break the queue", () =>
{
    Assert(DownloadProgress.Parse("CLIPDOCK_PROGRESS {\"status\":\"finished\",\"speed\":null,\"eta\":null}") is { Percent: 0, Finished: true });
    Assert(DownloadProgress.Parse("CLIPDOCK_PROGRESS {bad}") is null);
    Assert(DownloadProgress.Parse("CLIPDOCK_PROGRESS []") is null);
    Assert(DownloadProgress.Parse("normal log") is null);
});
Check("estimated progress is clamped", () => Assert(DownloadProgress.Parse("CLIPDOCK_PROGRESS {\"downloaded_bytes\":200,\"total_bytes_estimate\":100}")?.Percent == 100));
Check("separate video and audio keep one increasing progress bar until the completed file is ready", () =>
{
    var tracker = new DownloadProgressTracker(); tracker.SetParts("[\"137\",\"140\"]");
    Assert(tracker.Apply(new(50, 100, 2, false, "video")).Percent == 23.75);
    var video = tracker.Apply(new(100, 100, 0, true, "video"));
    Assert(video.Percent == 47.5 && !video.Finished);
    Assert(tracker.Apply(new(100, 100, 0, true, "video")).Percent == 47.5);
    Assert(tracker.Apply(new(0, 100, 2, false, "audio")).Percent == 47.5);
    var audio = tracker.Apply(new(100, 100, 0, true, "audio"));
    Assert(audio.Percent == 95 && audio.Finished);
    Assert(tracker.Apply(new(10, 100, 2, false, "audio")).Percent == 95);
});
// Engine arguments and output checks use one absolute folder that is valid on every OS.
var videos = Rooted("videos");
Check("CLI uses discrete arguments and URL terminator, ignores external configs", () =>
{
    var url = "https://example.com/v?x=\" & calc & echo \"";
    var start = DownloadPlan.Create(new DownloadRequest { Url = url, Title = "--exec calc" }, Rooted("tool space"), Rooted("video space"), new string('a', 32));
    Assert(!start.UseShellExecute && start.CreateNoWindow);
    Assert(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
    Assert(start.ArgumentList[^2] == "--" && start.ArgumentList[^1] == url);
    Assert(start.ArgumentList.Contains("--ignore-config") && start.ArgumentList.Contains("--no-playlist"));
    Assert(start.ArgumentList.Contains("bv*[height<=?1080]+ba/b[height<=?1080]"));
    Assert(start.ArgumentList.Contains("--recode-video") && start.ArgumentList.Contains("--abort-on-unavailable-fragments"));
    Assert(!start.ArgumentList.Contains("--cookies") && !start.ArgumentList.Contains("--cookies-from-browser"));
    Assert(!start.ArgumentList.Contains("--exec"));
});
Check("best quality omits height cap; all specified qualities are bounded", () =>
{
    foreach (var quality in new[] { "best", "480", "720", "1080", "2160" })
    {
        var args = DownloadPlan.Create(new DownloadRequest { Url = "https://a.test", Quality = quality }, Rooted("tools"), videos, new string('b', 32)).ArgumentList;
        Assert(args.Contains(quality == "best" ? "bv*+ba/b" : $"bv*[height<=?{quality}]+ba/b[height<=?{quality}]"));
    }
});
Check("format sort ranks resolution above the H.264/AAC preference", () =>
{
    var args = DownloadPlan.Create(new DownloadRequest { Url = "https://a.test", Quality = "2160" }, Rooted("tools"), videos, new string('c', 32)).ArgumentList;
    var fields = args[args.IndexOf("--format-sort") + 1].Split(',').ToList();
    Assert(fields.IndexOf("res") >= 0 && fields.IndexOf("res") < fields.IndexOf("vcodec:h264") && fields.IndexOf("vcodec:h264") < fields.IndexOf("acodec:aac"));
});
Check("MP3 extracts best available audio without a video height cap or video recoding", () =>
{
    var args = DownloadPlan.Create(new DownloadRequest { Url = "https://a.test", Quality = "480", Format = "mp3" }, Rooted("tools"), videos, new string('c', 32)).ArgumentList;
    Assert(args[args.IndexOf("--format") + 1] == "ba/b");
    Assert(args.Contains("--extract-audio") && args[args.IndexOf("--audio-format") + 1] == "mp3");
    Assert(!args.Contains("--recode-video") && !args.Contains("--merge-output-format") && !args.Any(a => a.Contains("height")));
    Assert(DownloadPlan.IsOutputFile(Path.Combine(videos, "audio.MP3"), videos, "mp3"));
    Assert(!DownloadPlan.IsOutputFile(Path.Combine(videos, "audio.mp4"), videos, "mp3"));
    Assert(!DownloadPlan.IsOutputFile(Path.Combine(videos, "audio.mp3"), videos, "mp4"));
    Assert(!DownloadPlan.IsOutputFile(Path.Combine(videos, "..", "audio.mp3"), videos, "mp3"));
});
Check("output verification tolerates missing or malformed paths", () =>
{
    Assert(!DownloadPlan.IsOutputFile(null, videos) && !DownloadPlan.IsOutputFile("", videos));
    Assert(!DownloadPlan.IsOutputFile(Path.Combine(videos, "clip.mp4"), null) && !DownloadPlan.IsOutputFile(Path.Combine(videos, "clip.mp4"), "  "));
    Assert(!DownloadPlan.IsOutputFile(Path.Combine(videos, "cl\0ip.mp4"), videos));
    Assert(DownloadPlan.IsOutputFile(Rooted("clip.MP4"), Rooted()));
});
Check("output verification rejects sibling path escapes and non-MP4", () =>
{
    Assert(DownloadPlan.IsOutputFile(Path.Combine(videos, "clip.mp4"), videos));
    Assert(!DownloadPlan.IsOutputFile(Rooted("videos-other", "clip.mp4"), videos));
    Assert(!DownloadPlan.IsOutputFile(Path.Combine(videos, "..", "secret.mp4"), videos));
    Assert(!DownloadPlan.IsOutputFile(Path.Combine(videos, "app.exe"), videos));
});
Check("DPAPI encrypts data at rest and rejects tampering", () =>
{
    if (!OperatingSystem.IsWindows()) throw new SkipCheckException("DPAPI exists only on Windows.");
    CheckProtection();
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void CheckProtection()
    {
        var bytes = Encoding.UTF8.GetBytes("private-download-history");
        var encrypted = WindowsProtection.Protect(bytes);
        Assert(!encrypted.AsSpan().SequenceEqual(bytes));
        Assert(WindowsProtection.Unprotect(encrypted).AsSpan().SequenceEqual(bytes));
        encrypted[^1] ^= 0x20;
        try { WindowsProtection.Unprotect(encrypted); throw new Exception("Tampered history was decrypted."); }
        catch (System.ComponentModel.Win32Exception) { }
    }
});
Check("engine update replaces executables, licenses and version metadata together", () =>
{
    WithToolPackage((destination, staging, names) =>
    {
        ToolInstaller.PromoteStagedPackage(staging, destination);
        foreach (var name in names) Assert(File.ReadAllText(Path.Combine(destination, name)) == "new");
        Assert(!Directory.EnumerateDirectories(destination, ".backup-*").Any());
    });
});
Check("a locked engine rolls back all earlier moves without losing the old package", () =>
{
    // Only Windows refuses to move a file that another handle has open.
    if (!OperatingSystem.IsWindows()) throw new SkipCheckException("needs Windows file locking.");
    WithToolPackage((destination, staging, names) =>
    {
        using var locked = new FileStream(Path.Combine(destination, AppPaths.Executable("ffprobe")), FileMode.Open, FileAccess.Read, FileShare.Read);
        Throws<IOException>(() => ToolInstaller.PromoteStagedPackage(staging, destination));
        foreach (var name in names) Assert(File.ReadAllText(Path.Combine(destination, name)) == "old");
        foreach (var name in names) Assert(File.ReadAllText(Path.Combine(staging, name)) == "new");
        Assert(!Directory.EnumerateDirectories(destination, ".backup-*").Any());
    });
});
Check("concurrent installers cannot remove each other's staging directories", () =>
{
    WithToolPackage((destination, staging, _) =>
    {
        using var held = ToolInstaller.AcquireInstallLock(destination);
        Throws<IOException>(() => ToolInstaller.InstallAsync(destination).GetAwaiter().GetResult());
        Assert(File.Exists(Path.Combine(staging, AppPaths.Executable("yt-dlp"))));
    });
});
Check("an incomplete engine package leaves the current installation untouched", () =>
{
    WithToolPackage((destination, staging, names) =>
    {
        File.Delete(Path.Combine(staging, "versions.json"));
        Throws<IOException>(() => ToolInstaller.PromoteStagedPackage(staging, destination));
        foreach (var name in names) Assert(File.ReadAllText(Path.Combine(destination, name)) == "old");
    });
});

void WithToolPackage(Action<string, string, string[]> check)
{
    var directory = Path.Combine(Path.GetTempPath(), "ClipDock-check-" + Guid.NewGuid().ToString("N"));
    var staging = Path.Combine(directory, ".staging-test");
    string[] names = [.. new[] { "yt-dlp", "ffmpeg", "ffprobe", "deno" }.Select(AppPaths.Executable), "versions.json", "licenses/NOTICE"];
    try
    {
        foreach (var (path, value) in new[] { (directory, "old"), (staging, "new") })
        {
            Directory.CreateDirectory(Path.Combine(path, "licenses"));
            foreach (var name in names) File.WriteAllText(Path.Combine(path, name), value);
        }
        check(directory, staging, names);
    }
    finally { Directory.Delete(directory, true); }
}


QueueChecks.Run(Check);
LibraryChecks.Run(Check);
EngineChecks.Run(Check);
BrowserOverlayChecks.Run(Check);
ToolInstallerChecks.Run(Check);
SessionChecks.Run(Check);
var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "this OS";
var result = $"{passed} passed; {failed} failed; {skipped} skipped on {platform} in {clock.Elapsed.TotalSeconds:0.0}s.";
Console.WriteLine(result);
// On GitHub Actions the run page lists failures and skips per OS without opening the log.
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
    File.AppendAllText(summary, $"### {(failed == 0 ? "✅" : "❌")} App checks on {platform}\n\n{result}\n\n" +
        (notes.Count > 0 ? $"<details{(failed > 0 ? " open" : "")}><summary>Failed and skipped checks</summary>\n\n{string.Join("\n", notes)}\n\n</details>\n\n" : ""));
if (filter is not null && passed + failed + skipped == 0) { Console.Error.WriteLine($"No check matches \"{filter}\"."); return 1; }
return failed == 0 ? 0 : 1;
