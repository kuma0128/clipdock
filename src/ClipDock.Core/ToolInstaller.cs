using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClipDock.Core;

public static class ToolInstaller
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(15) };
    private static readonly string[] Executables = ["yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "deno.exe"];
    static ToolInstaller() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipDock/1.0");

    public static bool IsReady(string path) => Executables.All(name => File.Exists(Path.Combine(path, name)));
    public static string FindTools() => IsReady(AppPaths.Tools) ? AppPaths.Tools : Path.Combine(AppContext.BaseDirectory, "tools");

    public static async Task InstallAsync(string destination, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        ct = deadline.Token;
        Directory.CreateDirectory(destination);
        var staging = Path.Combine(destination, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var versions = new List<object>();
        try
        {
            foreach (var (repo, assetName, names) in new (string, string, string[])[]
            {
                ("yt-dlp/yt-dlp", "yt-dlp.exe", ["yt-dlp.exe"]),
                ("yt-dlp/FFmpeg-Builds", "ffmpeg-master-latest-win64-gpl.zip", ["ffmpeg.exe", "ffprobe.exe"]),
                ("denoland/deno", "deno-x86_64-pc-windows-msvc.zip", ["deno.exe"])
            })
            {
                progress?.Report($"{repo} の配布情報を取得しています…");
                using var release = await Client.GetFromJsonAsync<JsonDocument>($"https://api.github.com/repos/{repo}/releases/latest", ct)
                    ?? throw new IOException("配布情報を取得できませんでした。");
                var asset = release.RootElement.GetProperty("assets").EnumerateArray().First(a => a.GetProperty("name").GetString() == assetName);
                var url = asset.GetProperty("browser_download_url").GetString()!;
                var digest = asset.GetProperty("digest").GetString();
                if (digest is null || !digest.StartsWith("sha256:") || digest.Length != 71)
                    throw new IOException($"{assetName} のSHA-256情報がありません。未検証のファイルはインストールしません。");
                var archive = Path.Combine(staging, assetName);
                using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = File.Create(archive);
                    var buffer = new byte[128 * 1024];
                    long received = 0;
                    long reported = -1;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, ct);
                        if (count == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        received += count;
                        var mb = received / 1024 / 1024;
                        if (mb != reported) { progress?.Report($"{assetName} を取得中 · {mb} MB"); reported = mb; }
                    }
                }
                await using (var stream = File.OpenRead(archive))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                    if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new IOException($"{assetName} の整合性確認に失敗しました。再実行してください。");
                }
                if (assetName.EndsWith(".zip"))
                {
                    using var zip = ZipFile.OpenRead(archive);
                    foreach (var name in names)
                    {
                        var entry = zip.Entries.Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        entry.ExtractToFile(Path.Combine(staging, name));
                    }
                    var licenseFolder = Path.Combine(staging, "licenses", repo.Replace('/', '-'));
                    Directory.CreateDirectory(licenseFolder);
                    foreach (var entry in zip.Entries.Where(e => e.Name.Contains("license", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("copying", StringComparison.OrdinalIgnoreCase)))
                        if (entry.Name.Length > 0) entry.ExtractToFile(Path.Combine(licenseFolder, Path.GetFileName(entry.Name)), true);
                }
                versions.Add(new { repository = repo, tag = release.RootElement.GetProperty("tag_name").GetString(), asset = assetName, url, sha256 = digest[7..] });
            }
            ct.ThrowIfCancellationRequested();
            foreach (var name in Executables) File.Move(Path.Combine(staging, name), Path.Combine(destination, name), true);
            var licenses = Path.Combine(staging, "licenses");
            if (Directory.Exists(licenses))
            {
                foreach (var file in Directory.EnumerateFiles(licenses, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(destination, "licenses", Path.GetRelativePath(licenses, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(destination, "versions.json"), JsonSerializer.Serialize(versions, Json.Options), ct);
            progress?.Report("ダウンロードエンジンの準備ができました。");
        }
        finally { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
