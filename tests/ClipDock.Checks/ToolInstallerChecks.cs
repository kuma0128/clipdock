using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClipDock.Core;
using static CheckAssert;

internal static class ToolInstallerChecks
{
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static void Run(Action<string, Action> check)
    {
        // The fixture serves the Windows engine set (EXE and ZIP assets); macOS fetches a different set.
        void Async(string name, Func<Task> action) => check(name, () =>
        {
            if (!OperatingSystem.IsWindows()) throw new SkipCheckException("the fixture serves the Windows engine set.");
            action().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        });

        Async("engine update verifies every local file and skips all downloads and file rewrites when current", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            Assert(fixture.AssetRequests.Count == 3);
            Assert(ToolInstaller.IsReady(fixture.Destination));
            foreach (var path in Directory.EnumerateFiles(fixture.Destination, "*", SearchOption.AllDirectories))
                File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var before = fixture.Snapshot();
            fixture.Reset();
            await fixture.InstallAsync();
            Assert(fixture.Requests.Count == 3 && fixture.Requests.All(url => url.Host == "api.github.com"));
            fixture.AssertSnapshot(before);
            Assert(fixture.Messages.Last().Contains("最新版") && fixture.Messages.Last().Contains("再ダウンロードは不要"));
            Assert(!Directory.EnumerateDirectories(fixture.Destination, ".staging-*").Any());
        });

        Async("a corrupt extracted executable repairs only its archive and retains other components and licenses", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            var before = fixture.Snapshot();
            await File.WriteAllTextAsync(Path.Combine(fixture.Destination, "ffprobe.exe"), "corrupt");
            fixture.Reset();
            await fixture.InstallAsync();
            Assert(fixture.AssetRequests.SequenceEqual(["ffmpeg-master-latest-win64-gpl.zip"]));
            Assert(fixture.Requests.Count == 4, "unchanged components and licenses must not be downloaded");
            fixture.AssertContents(before);
        });

        Async("missing executables and missing or corrupt licenses trigger repair of their component only", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            var before = fixture.Snapshot();
            foreach (var (path, asset, corrupt) in new[]
            {
                ("deno.exe", "deno-x86_64-pc-windows-msvc.zip", false),
                ("licenses/yt-dlp-yt-dlp/LICENSE", "yt-dlp.exe", false),
                ("licenses/yt-dlp-yt-dlp/THIRD_PARTY_LICENSES.txt", "yt-dlp.exe", true)
            })
            {
                if (corrupt) await File.WriteAllTextAsync(Path.Combine(fixture.Destination, path), "corrupt");
                else File.Delete(Path.Combine(fixture.Destination, path));
                fixture.Reset();
                await fixture.InstallAsync();
                Assert(fixture.AssetRequests.SequenceEqual([asset]));
                fixture.AssertContents(before);
            }
        });

        Async("a changed release digest replaces its component even when the release tag and URL are unchanged", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            var oldYtDlp = await File.ReadAllBytesAsync(Path.Combine(fixture.Destination, "yt-dlp.exe"));
            fixture.Releases["yt-dlp/FFmpeg-Builds"].Update("second");
            fixture.Reset();
            await fixture.InstallAsync();
            Assert(fixture.AssetRequests.SequenceEqual(["ffmpeg-master-latest-win64-gpl.zip"]));
            Assert((await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "ffmpeg.exe"))).Contains("second"));
            var unchangedYtDlp = await File.ReadAllBytesAsync(Path.Combine(fixture.Destination, "yt-dlp.exe"));
            Assert(oldYtDlp.SequenceEqual(unchangedYtDlp));
            fixture.Reset();
            await fixture.InstallAsync();
            Assert(fixture.AssetRequests.Count == 0);
        });

        Async("legacy version labels without verified file hashes require one repair before reuse", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            var manifestPath = Path.Combine(fixture.Destination, "versions.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsArray();
            foreach (var entry in manifest) entry!.AsObject().Remove("files");
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            fixture.Reset();
            await fixture.InstallAsync();
            Assert(fixture.AssetRequests.Count == 3);
            fixture.Reset();
            await fixture.InstallAsync();
            Assert(fixture.AssetRequests.Count == 0);
        });

        Async("a bad downloaded digest leaves the installed set untouched and cleans staging", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            var before = fixture.Snapshot();
            var release = fixture.Releases["denoland/deno"];
            release.Update("new");
            release.ServedContent = Encoding.UTF8.GetBytes("corrupt download");
            fixture.Reset();
            await ThrowsAsync<IOException>(() => fixture.InstallAsync());
            Assert(fixture.AssetRequests.SequenceEqual(["deno-x86_64-pc-windows-msvc.zip"]));
            fixture.AssertSnapshot(before);
            Assert(!Directory.EnumerateDirectories(fixture.Destination, ".staging-*").Any());
            Assert(!Directory.EnumerateDirectories(fixture.Destination, ".backup-*").Any());
        });

        Async("missing upstream digest cannot authorize a cached version or modify installed files", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            var before = fixture.Snapshot();
            fixture.Releases["yt-dlp/FFmpeg-Builds"].MissingDigest = true;
            fixture.Reset();
            await ThrowsAsync<IOException>(() => fixture.InstallAsync());
            Assert(fixture.AssetRequests.Count == 0);
            fixture.AssertSnapshot(before);
        });

        Async("cached file paths cannot copy files outside their component during an update", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            var sentinel = Path.Combine(fixture.Root, "outside.txt");
            await File.WriteAllTextAsync(sentinel, "outside");
            var manifestPath = Path.Combine(fixture.Destination, "versions.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsArray();
            manifest[0]!["files"]!["../outside.txt"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("outside")));
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            fixture.Reset();
            await fixture.InstallAsync();
            Assert(fixture.AssetRequests.SequenceEqual(["yt-dlp.exe"]));
            Assert(await File.ReadAllTextAsync(sentinel) == "outside");
            Assert(!File.Exists(Path.Combine(fixture.Destination, "outside.txt")));
        });

        Async("unchanged components are verified again after copying before the staged set is promoted", async () =>
        {
            using var fixture = new Fixture();
            await fixture.InstallAsync();
            var oldManifest = await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "versions.json"));
            var oldYtDlp = await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "yt-dlp.exe"));
            fixture.Releases["yt-dlp/yt-dlp"].Update("new");
            fixture.OnRequest = url =>
            {
                if (url.Host == "downloads.example" && url.AbsolutePath.EndsWith("yt-dlp.exe"))
                    File.WriteAllText(Path.Combine(fixture.Destination, "ffmpeg.exe"), "changed during update");
            };
            await ThrowsAsync<IOException>(() => fixture.InstallAsync());
            Assert(await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "versions.json")) == oldManifest);
            Assert(await File.ReadAllTextAsync(Path.Combine(fixture.Destination, "yt-dlp.exe")) == oldYtDlp);
            Assert(!Directory.EnumerateDirectories(fixture.Destination, ".staging-*").Any());
        });
    }

    private sealed class Release(string repository, string asset, params string[] executables)
    {
        public string Repository { get; } = repository;
        public string Asset { get; } = asset;
        public string Tag => "v1";
        public string Url => $"https://downloads.example/{Repository}/{Asset}";
        public byte[] Content { get; private set; } = [];
        public byte[]? ServedContent { get; set; }
        public bool MissingDigest { get; set; }
        public void Update(string revision)
        {
            if (!Asset.EndsWith(".zip")) { Content = Encoding.UTF8.GetBytes("executable " + revision); return; }
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                foreach (var name in executables) Write("package/bin/" + name, name + " " + revision);
                if (Repository == "yt-dlp/FFmpeg-Builds") Write("package/LICENSE.txt", "license " + revision);
                void Write(string path, string text)
                {
                    var entry = zip.CreateEntry(path);
                    using var output = new StreamWriter(entry.Open());
                    output.Write(text);
                }
            }
            Content = bytes.ToArray();
        }
    }

    private sealed class Fixture : IDisposable, IProgress<string>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ClipDock-installer-check-" + Guid.NewGuid().ToString("N"));
        public string Destination => Path.Combine(Root, "tools");
        public Dictionary<string, Release> Releases { get; }
        public List<Uri> Requests { get; } = [];
        public List<string> Messages { get; } = [];
        public List<string> AssetRequests => Requests.Where(url => url.Host == "downloads.example").Select(url => Path.GetFileName(url.AbsolutePath)).ToList();
        public Action<Uri>? OnRequest { get; set; }
        private readonly HttpClient client;
        public Fixture()
        {
            Releases = new[]
            {
                new Release("yt-dlp/yt-dlp", "yt-dlp.exe", "yt-dlp.exe"),
                new Release("yt-dlp/FFmpeg-Builds", "ffmpeg-master-latest-win64-gpl.zip", "ffmpeg.exe", "ffprobe.exe"),
                new Release("denoland/deno", "deno-x86_64-pc-windows-msvc.zip", "deno.exe")
            }.ToDictionary(release => release.Repository);
            foreach (var release in Releases.Values) release.Update("first");
            client = new HttpClient(new Handler(this));
        }
        public Task InstallAsync() => ToolInstaller.InstallWithClientAsync(Destination, client, this);
        public void Reset() { Requests.Clear(); Messages.Clear(); }
        public void Report(string value) => Messages.Add(value);
        public Dictionary<string, (string Hash, DateTime Written)> Snapshot() => Directory.EnumerateFiles(Destination, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(Destination, path), path => (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path)));
        public void AssertSnapshot(Dictionary<string, (string Hash, DateTime Written)> before)
        {
            var after = Snapshot();
            Assert(before.Count == after.Count);
            foreach (var (path, value) in before) Assert(after.TryGetValue(path, out var actual) && actual == value, "Unexpected write: " + path);
        }
        public void AssertContents(Dictionary<string, (string Hash, DateTime Written)> before)
        {
            var after = Snapshot();
            Assert(before.Count == after.Count);
            foreach (var (path, value) in before) Assert(after.TryGetValue(path, out var actual) && actual.Hash == value.Hash, "Unexpected content: " + path);
        }
        public void Dispose()
        {
            client.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = request.RequestUri!;
                fixture.Requests.Add(url);
                fixture.OnRequest?.Invoke(url);
                HttpContent content;
                if (url.Host == "api.github.com")
                {
                    var repository = url.AbsolutePath["/repos/".Length..^"/releases/latest".Length];
                    var release = fixture.Releases[repository];
                    content = new StringContent(JsonSerializer.Serialize(new
                    {
                        tag_name = release.Tag,
                        assets = new[] { new { name = release.Asset, browser_download_url = release.Url,
                            digest = release.MissingDigest ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(release.Content)) } }
                    }), Encoding.UTF8, "application/json");
                }
                else if (url.Host == "downloads.example")
                {
                    var release = fixture.Releases.Values.Single(release => release.Url == url.AbsoluteUri);
                    content = new ByteArrayContent(release.ServedContent ?? release.Content);
                }
                else if (url.Host == "raw.githubusercontent.com") content = new StringContent("License " + url.AbsolutePath);
                else throw new InvalidOperationException("Unexpected network request: " + url);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        }
    }
}
