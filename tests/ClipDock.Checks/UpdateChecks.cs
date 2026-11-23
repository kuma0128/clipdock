using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClipDock.Core;
using static CheckAssert;

// Self-update trusts the official site over HTTPS; these checks pin down what it accepts from the manifest
// and that nothing unverified is ever kept as an installer.
internal static class UpdateChecks
{
    private static readonly Uri Manifest = new("https://clipdock-d6z.pages.dev/updates/latest.json");
    private static readonly string Hash = new('a', 64);

    private static string Json(string version = "1.0.5", string platform = "win-x64", string? url = null, string? sha256 = null, string size = "1000") =>
        "{\"version\":\"" + version + "\",\"files\":{\"" + platform + "\":{\"url\":\"" + (url ?? $"/downloads/ClipDock-{version}-win-x64-unsigned-setup.exe") +
        "\",\"size\":" + size + ",\"sha256\":\"" + (sha256 ?? Hash) + "\"}}}";

    private sealed class Server(Func<HttpRequestMessage, byte[]> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(respond(request)) });
        }
    }

    public static void Run(Action<string, Action> check)
    {
        var current = new Version(1, 0, 4);
        check("only a newer release with a checksum and a download on the official site is offered", () =>
        {
            var offer = AppUpdate.Parse(Json(), current, "win-x64", Manifest)!;
            Assert(offer.Version == new Version(1, 0, 5) && offer.Size == 1000 && offer.Sha256 == Hash);
            Assert(offer.Url.AbsoluteUri == "https://clipdock-d6z.pages.dev/downloads/ClipDock-1.0.5-win-x64-unsigned-setup.exe");
            Assert(offer.FileName == "ClipDock-1.0.5-win-x64-unsigned-setup.exe");
            Assert(AppUpdate.Parse(Json(sha256: Hash.ToUpperInvariant()), current, "win-x64", Manifest)!.Sha256 == Hash);
            Assert(AppUpdate.Parse(Json(url: "https://clipdock-d6z.pages.dev/downloads/ClipDock-1.0.5-win-x64-unsigned-setup.exe"), current, "win-x64", Manifest) is not null);

            // The same, an older or another platform's release is not an update.
            Assert(AppUpdate.Parse(Json("1.0.4"), current, "win-x64", Manifest) is null);
            Assert(AppUpdate.Parse(Json("1.0.3"), new Version(1, 0, 4, 0), "win-x64", Manifest) is null);
            Assert(AppUpdate.Parse(Json(), current, "mac-arm64", Manifest) is null);
            Assert(AppUpdate.Parse(Json("1.1.0"), new Version(1, 0, 9), "win-x64", Manifest) is not null);

            foreach (var json in new[]
            {
                Json(url: "https://example.com/downloads/ClipDock-1.0.5-win-x64-unsigned-setup.exe"),
                Json(url: "http://clipdock-d6z.pages.dev/downloads/ClipDock-1.0.5-win-x64-unsigned-setup.exe"),
                Json(url: "/downloads/ClipDock-1.0.4-win-x64-unsigned-setup.exe"),
                Json(url: "/other/ClipDock-1.0.5-win-x64-unsigned-setup.exe"),
                Json(url: "/downloads/ClipDock-1.0.5-win-x64-unsigned-setup.exe?x=1"),
                Json(url: "//example.com/downloads/ClipDock-1.0.5-win-x64-unsigned-setup.exe"),
                Json(sha256: "abc"), Json(sha256: new string('g', 64)), Json(size: "0"), Json(size: "999999999999"), Json(size: "\"1000\""),
                Json("1.0"), Json("v1.0.5"), Json("1.0.5-beta"), Json("1.0.5.1"), "{}", "[]", "{\"version\":5}"
            })
                Throws<InvalidDataException>(() => AppUpdate.Parse(json, current, "win-x64", Manifest));
            Throws<System.Text.Json.JsonException>(() => AppUpdate.Parse("not json", current, "win-x64", Manifest));
        });

        check("the manifest is read fresh and an update download is kept only when its size and SHA-256 match", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "clipdock-update-" + Guid.NewGuid().ToString("N"));
            try
            {
                var installer = Encoding.UTF8.GetBytes("pretend installer bytes");
                var hash = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
                var manifest = Json(sha256: hash, size: installer.Length.ToString());
                var server = new Server(request => request.RequestUri!.AbsolutePath == Manifest.AbsolutePath
                    ? (request.Headers.CacheControl?.NoCache == true ? Encoding.UTF8.GetBytes(manifest) : [])
                    : installer);
                using var client = new HttpClient(server);
                var offer = AppUpdate.CheckWithClientAsync(client, Manifest, current, "win-x64", default).GetAwaiter().GetResult()!;
                Assert(offer.Sha256 == hash);

                Directory.CreateDirectory(directory);
                var stale = Path.Combine(directory, "ClipDock-1.0.3-win-x64-unsigned-setup.exe");
                File.WriteAllText(stale, "old release");
                var file = AppUpdate.DownloadWithClientAsync(client, offer, directory, default).GetAwaiter().GetResult();
                Assert(File.ReadAllBytes(file).AsSpan().SequenceEqual(installer) && Path.GetFileName(file) == offer.FileName);
                Assert(!File.Exists(stale) && Directory.GetFiles(directory).Length == 1);
                var requests = server.Requests;
                Assert(AppUpdate.DownloadWithClientAsync(client, offer, directory, default).GetAwaiter().GetResult() == file && server.Requests == requests,
                    "a verified download must be reused");

                // A tampered or truncated file on disk is fetched again rather than trusted.
                File.WriteAllText(file, "tampered");
                AppUpdate.DownloadWithClientAsync(client, offer, directory, default).GetAwaiter().GetResult();
                Assert(File.ReadAllBytes(file).AsSpan().SequenceEqual(installer) && server.Requests == requests + 1);

                foreach (var wrong in new[] { offer with { Sha256 = new string('0', 64) }, offer with { Size = installer.Length - 1 }, offer with { Size = installer.Length + 1 } })
                {
                    File.Delete(file);
                    Throws<InvalidDataException>(() => AppUpdate.DownloadWithClientAsync(client, wrong, directory, default).GetAwaiter().GetResult());
                    Assert(Directory.GetFiles(directory).Length == 0, "nothing unverified may stay behind");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });

        check("the Mac update script swaps in the bundle from the disk image after ClipDock quits and keeps it otherwise", () =>
        {
            if (!OperatingSystem.IsMacOS()) throw new SkipCheckException("needs hdiutil and PlistBuddy.");
            var root = Path.Combine(Path.GetTempPath(), "clipdock-mac-update-" + Guid.NewGuid().ToString("N"));
            try
            {
                var installed = Bundle(Path.Combine(root, "Applications"), "1.0.4", "app.clipdock.ClipDock");
                Assert(RunScript(root, installed, Image(root, "1.0.5", "app.clipdock.ClipDock"), "1.0.5") == 0);
                Assert(VersionOf(installed) == "1.0.5" && File.ReadAllText(Path.Combine(installed, "Contents", "MacOS", "ClipDock")) == "1.0.5");
                Assert(Directory.GetFileSystemEntries(Path.Combine(root, "Applications")).Select(Path.GetFileName).SequenceEqual(["ClipDock.app"]),
                    "no staged or previous bundle may be left beside the app");

                // A disk image holding another version or another app never replaces the installed bundle.
                foreach (var (version, id) in new[] { ("1.0.6", "app.clipdock.ClipDock"), ("1.0.7", "com.example.Other") })
                {
                    Assert(RunScript(root, installed, Image(root, version, id), "1.0.7") == 1);
                    Assert(VersionOf(installed) == "1.0.5");
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

            static string Bundle(string folder, string version, string id)
            {
                var bundle = Path.Combine(folder, "ClipDock.app");
                Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
                File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"),
                    $"""<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>CFBundleIdentifier</key><string>{id}</string><key>CFBundleShortVersionString</key><string>{version}</string></dict></plist>""");
                File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "ClipDock"), version);
                return bundle;
            }
            static string Image(string root, string version, string id)
            {
                var source = Path.Combine(root, "image-" + version);
                Bundle(source, version, id);
                var image = Path.Combine(root, $"update-{version}.dmg");
                Assert(Run("/usr/bin/hdiutil", "create", "-volname", "ClipDock", "-srcfolder", source, "-ov", "-format", "UDZO", image) == 0);
                return image;
            }
            static string VersionOf(string bundle) => System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(Path.Combine(bundle, "Contents", "Info.plist")), "CFBundleShortVersionString</key><string>([^<]+)").Groups[1].Value;
            // Waits for a stand-in ClipDock (a short sleep) to end, as the real script waits for the app to quit.
            static int RunScript(string root, string bundle, string image, string version)
            {
                var script = Path.Combine(root, "update.sh");
                File.WriteAllText(script, ClipDock.Mac.MacUpdater.Script);
                using var app = System.Diagnostics.Process.Start("/bin/sleep", "1")!;
                var status = Run("/bin/sh", script, app.Id.ToString(), image, bundle, version, "none", Path.Combine(root, "update.log"));
                Assert(!File.Exists(image) && !File.Exists(script), "the script removes the disk image and itself");
                return status;
            }
            static int Run(string file, params string[] arguments)
            {
                var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start)!;
                if (!process.WaitForExit(120_000)) throw new TimeoutException(file + " did not finish");
                return process.ExitCode;
            }
        });
    }
}
