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
        // A crash or power loss mid-install leaves hundreds of MB behind; nothing else uses these folders.
        foreach (var stale in Directory.EnumerateDirectories(destination, ".staging-*"))
            try { Directory.Delete(stale, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
                using var release = await GetReleaseAsync(repo, ct);
                var asset = release.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                    ? assets.EnumerateArray().FirstOrDefault(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == assetName)
                    : default;
                if (asset.ValueKind != JsonValueKind.Object)
                    throw new IOException($"{repo} の最新リリースに {assetName} が見つかりません。時間をおいて再実行してください。");
                string? Text(string key) => asset.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var url = Text("browser_download_url");
                if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var location) || location.Scheme != Uri.UriSchemeHttps)
                    throw new IOException($"{assetName} の取得先が正しくありません。");
                var digest = Text("digest");
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
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
                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
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
                var tag = release.RootElement.GetProperty("tag_name").GetString()!;
                var licenseFiles = repo switch
                {
                    "yt-dlp/yt-dlp" => new[] { "LICENSE", "THIRD_PARTY_LICENSES.txt" },
                    "denoland/deno" => ["LICENSE.md"],
                    _ => Array.Empty<string>()
                };
                foreach (var licenseFile in licenseFiles)
                {
                    var text = await Client.GetStringAsync($"https://raw.githubusercontent.com/{repo}/{Uri.EscapeDataString(tag)}/{licenseFile}", ct);
                    var folder = Path.Combine(staging, "licenses", repo.Replace('/', '-'));
                    Directory.CreateDirectory(folder);
                    await File.WriteAllTextAsync(Path.Combine(folder, licenseFile), text, ct);
                }
                versions.Add(new { repository = repo, tag, asset = assetName, url, sha256 = digest[7..] });
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

    private static async Task<JsonDocument> GetReleaseAsync(string repo, CancellationToken ct)
    {
        try
        {
            return await Client.GetFromJsonAsync<JsonDocument>($"https://api.github.com/repos/{repo}/releases/latest", ct)
                ?? throw new IOException("配布情報を取得できませんでした。");
        }
        // Unauthenticated GitHub API calls are limited per IP address (shared offices, VPNs).
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new IOException("GitHubの利用制限に達しました。1時間ほど待ってから再実行してください。", ex);
        }
        catch (JsonException ex) { throw new IOException($"{repo} の配布情報を読み取れませんでした。", ex); }
    }
}
