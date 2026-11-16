using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClipDock.Core;
using static CheckAssert;

internal static class SessionChecks
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool OnlyCurrentUserAllowed(string path)
    {
        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        return rules.All(r => r.AccessControlType != AccessControlType.Allow || r.IdentityReference.Equals(WindowsIdentity.GetCurrent().User));
    }

    public static void Run(Action<string, Action> check)
    {
        check("login is on for new and migrated settings; an explicit opt-out survives restart", () =>
        {
            foreach (var json in new[] { "{}", "{\"quality\":\"1080\",\"format\":\"mp3\"}" })
            {
                Assert(JsonSerializer.Deserialize<AppSettings>(json, Json.Options)!.UseLogin);
                Assert(JsonSerializer.Deserialize<DownloadRequest>(json, Json.Options)!.UseLogin);
            }
            var saved = JsonSerializer.Serialize(new AppSettings("videos", "1080", UseLogin: false), Json.Options);
            Assert(!JsonSerializer.Deserialize<AppSettings>(saved, Json.Options)!.UseLogin);
            var request = new DownloadRequest { Url = "https://x.com/example/status/123" };
            var cookies = Rooted("sessions", "login.cookies");
            var args = DownloadPlan.Create(request, Rooted("tools"), Rooted("videos"), new string('d', 32), cookies).ArgumentList;
            Assert(args.Contains("--cookies") && args[args.IndexOf("--cookies") + 1] == cookies);
            Assert(!DownloadPlan.Create(request with { UseLogin = false }, Rooted("tools"), Rooted("videos"), new string('d', 32), cookies).ArgumentList.Contains("--cookies"));
            Throws<ArgumentException>(() => DownloadPlan.Create(request, Rooted("tools"), Rooted("videos"), new string('d', 32), "login.cookies"));
        });
        check("YouTube home and search URLs are rejected before saving while video URLs remain valid", () =>
        {
            foreach (var url in new[] { "https://youtube.com/", "https://www.youtube.com/watch", "https://m.youtube.com/results?search_query=video", "https://www.youtube.com/@example", "https://youtu.be/" })
            {
                var request = new DownloadRequest { Url = url }.Validated();
                try { request.ValidateVideoPage(); throw new Exception("Non-video URL accepted."); }
                catch (ArgumentException ex) { Assert(ex.Message.Contains("動画ページ")); }
            }
            foreach (var url in new[] { "https://www.youtube.com/watch?v=abcdefghijk", "https://youtu.be/abcdefghijk?t=12", "https://www.youtube.com/shorts/abcdefghijk", "https://youtube.com/live/abcdefghijk", "https://youtube-nocookie.com/embed/abcdefghijk", "https://example.com/video.mp4" })
                new DownloadRequest { Url = url }.ValidateVideoPage();
        });
        check("login cookies are scoped to the requested service and lookalike domains are excluded", () =>
        {
            Assert(SiteSessionScope.AllowsCookie("https://x.com/a/status/123", ".x.com"));
            Assert(SiteSessionScope.AllowsCookie("https://x.com/a/status/123", ".twitter.com"));
            foreach (var domain in new[] { ".google.com", ".youtube.com", "x.com.evil.test", "notx.com" })
                Assert(!SiteSessionScope.AllowsCookie("https://x.com/a/status/123", domain));
            Assert(SiteSessionScope.AllowsCookie("https://youtu.be/abcdefghijk", ".youtube.com"));
            Assert(!SiteSessionScope.AllowsCookie("https://youtu.be/abcdefghijk", ".google.com"));
            Assert(!SiteSessionScope.AllowsCookie("https://x.com.evil.test/video", ".x.com"));
            Assert(SiteSessionScope.CookieOrigins("https://video.example.org/watch").SequenceEqual(new[] { "https://video.example.org/watch" }));
            Assert(SiteSessionScope.AllowsCookie("https://video.example.org/watch", ".example.org") && !SiteSessionScope.AllowsCookie("https://video.example.org/watch", ".other.org"));
            Assert(!SiteSessionScope.AllowsNavigation("http://x.com/") && !SiteSessionScope.AllowsNavigation("file:///C:/private"));
        });
        check("temporary cookies exclude unsafe and expired fields, restrict access, redact values and are deleted", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ClipDock-session-check-" + Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "sessions");
            Directory.CreateDirectory(root);
            try
            {
                var source = new[]
                {
                    new SessionCookie(".x.com", "/", "auth_token", "synthetic-private-value", true, true, 0),
                    new SessionCookie(".youtube.com", "/", "SID", "other-service", true, true, 0),
                    new SessionCookie(".x.com", "/", "bad", "bad\ninjected", true, true, 0),
                    new SessionCookie(".x.com", "/", "expired", "old-value", true, false, 1)
                };
                string path;
                using (var file = SessionCookieFile.Create("https://x.com/a/status/123", source, directory) ?? throw new Exception("Session file missing."))
                {
                    path = file.FilePath;
                    var lines = File.ReadAllLines(path);
                    Assert(lines.Length == 2 && lines[1].StartsWith("#HttpOnly_.x.com\tTRUE\t/\tTRUE\t0\tauth_token\t"));
                    Assert(!file.Redact("error synthetic-private-value " + path).Contains("synthetic-private-value"));
                    if (OperatingSystem.IsWindows()) Assert(OnlyCurrentUserAllowed(path));
                    else
                    {
                        Assert(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
                        Assert(File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
                    }
                }
                Assert(!File.Exists(path));
                Assert(SessionCookieFile.Create("https://example.com/video", source, directory) is null);
            }
            finally { Directory.Delete(root, true); }
        });
    }
}
