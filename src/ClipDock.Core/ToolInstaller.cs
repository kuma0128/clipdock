using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

[assembly: InternalsVisibleTo("ClipDock.Checks")]

namespace ClipDock.Core;

public static class ToolInstaller
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(15) };
    private static readonly string[] Executables = ["yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "deno.exe"];
    private static readonly Component[] Components =
    [
        new("yt-dlp/yt-dlp", "yt-dlp.exe", ["yt-dlp.exe"], ["LICENSE", "THIRD_PARTY_LICENSES.txt"]),
        new("yt-dlp/FFmpeg-Builds", "ffmpeg-master-latest-win64-gpl.zip", ["ffmpeg.exe", "ffprobe.exe"], []),
        new("denoland/deno", "deno-x86_64-pc-windows-msvc.zip", ["deno.exe"], ["LICENSE.md"])
    ];
    private sealed record Component(string Repository, string Asset, string[] Names, string[] Licenses)
    {
        public string LicenseFolder => "licenses/" + Repository.Replace('/', '-');
    }
    private sealed record InstalledComponent(string Repository, string Tag, string Asset, string Url, string Sha256,
        Dictionary<string, string>? Files = null);
    static ToolInstaller() => Client.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipDock/{typeof(ToolInstaller).Assembly.GetName().Version!.ToString(3)}");

    public static bool IsReady(string path) => Executables.All(name => File.Exists(Path.Combine(path, name)));
    public static string FindTools() => IsReady(AppPaths.Tools) ? AppPaths.Tools : Path.Combine(AppContext.BaseDirectory, "tools");

    public static Task InstallAsync(string destination, IProgress<string>? progress = null, CancellationToken ct = default)
        => InstallWithClientAsync(destination, Client, progress, ct);

    internal static async Task InstallWithClientAsync(string destination, HttpClient client, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        ct = deadline.Token;
        ct.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        // This also protects staging cleanup from another installer process.
        using var installLock = AcquireInstallLock(destination);
        // A crash or power loss mid-install leaves hundreds of MB behind; nothing else uses these folders.
        foreach (var stale in Directory.EnumerateDirectories(destination, ".staging-*"))
            try { Directory.Delete(stale, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var installed = ReadInstalled(destination);
        var plans = new List<(Component Component, InstalledComponent Release, bool Reuse)>();
        foreach (var component in Components)
        {
            progress?.Report($"{component.Repository} の配布情報を取得しています…");
            var release = await ReadReleaseAsync(component, client, ct);
            var previous = installed.Where(v => v.Repository == component.Repository && v.Asset == component.Asset).ToArray();
            var reusable = previous.Length == 1 && previous[0].Tag == release.Tag &&
                release.Sha256.Equals(previous[0].Sha256, StringComparison.OrdinalIgnoreCase) &&
                await VerifyInstalledAsync(destination, component, previous[0].Files, ct);
            if (reusable)
            {
                release = release with { Files = previous[0].Files };
                progress?.Report($"{component.Asset} は最新版です。");
            }
            plans.Add((component, release, reusable));
        }
        ct.ThrowIfCancellationRequested();
        if (plans.All(plan => plan.Reuse))
        {
            progress?.Report("ダウンロードエンジンは最新版です。再ダウンロードは不要です。");
            return;
        }

        var staging = Path.Combine(destination, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var versions = new List<InstalledComponent>();
            foreach (var (component, release, reuse) in plans)
                versions.Add(reuse
                    ? await CopyInstalledAsync(destination, staging, release, ct)
                    : await DownloadComponentAsync(staging, component, release, client, progress, ct));
            await File.WriteAllTextAsync(Path.Combine(staging, "versions.json"), JsonSerializer.Serialize(versions, Json.Options), ct);
            ct.ThrowIfCancellationRequested();
            PromoteStagedPackage(staging, destination);
            progress?.Report("ダウンロードエンジンの準備ができました。");
        }
        finally { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static List<InstalledComponent> ReadInstalled(string destination)
    {
        try { return JsonSerializer.Deserialize<List<InstalledComponent>>(File.ReadAllText(Path.Combine(destination, "versions.json")), Json.Options)?.Where(v => v is not null).ToList() ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static async Task<InstalledComponent> ReadReleaseAsync(Component component, HttpClient client, CancellationToken ct)
    {
        using var release = await GetReleaseAsync(component.Repository, client, ct);
        var asset = release.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray().FirstOrDefault(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == component.Asset)
            : default;
        if (asset.ValueKind != JsonValueKind.Object)
            throw new IOException($"{component.Repository} の最新リリースに {component.Asset} が見つかりません。時間をおいて再実行してください。");
        string? Text(string key) => asset.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var url = Text("browser_download_url");
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var location) || location.Scheme != Uri.UriSchemeHttps)
            throw new IOException($"{component.Asset} の取得先が正しくありません。");
        var digest = Text("digest");
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..]))
            throw new IOException($"{component.Asset} のSHA-256情報がありません。未検証のファイルはインストールしません。");
        if (!release.RootElement.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tag.GetString()))
            throw new IOException($"{component.Repository} のリリース識別情報がありません。");
        return new(component.Repository, tag.GetString()!, component.Asset, url, digest[7..]);
    }

    private static bool IsSha256(string? hash) => hash?.Length == 64 && hash.All(char.IsAsciiHexDigit);

    private static bool HasExpectedFiles(Component component, Dictionary<string, string>? files)
    {
        if (files is null || !component.Names.All(files.ContainsKey) ||
            !component.Licenses.All(name => files.ContainsKey(component.LicenseFolder + "/" + name)) ||
            !files.Keys.Any(path => path.StartsWith(component.LicenseFolder + "/", StringComparison.Ordinal))) return false;
        foreach (var (path, hash) in files)
        {
            if (!IsSha256(hash)) return false;
            if (component.Names.Contains(path)) continue;
            if (!path.StartsWith(component.LicenseFolder + "/", StringComparison.Ordinal)) return false;
            var name = path[(component.LicenseFolder.Length + 1)..];
            if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/')) return false;
        }
        return true;
    }

    private static async Task<bool> VerifyInstalledAsync(string destination, Component component, Dictionary<string, string>? files, CancellationToken ct)
    {
        if (!HasExpectedFiles(component, files)) return false;
        try
        {
            foreach (var (path, hash) in files!)
                if (!hash.Equals(await HashFileAsync(Path.Combine(destination, path), ct), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task<InstalledComponent> CopyInstalledAsync(string destination, string staging, InstalledComponent release, CancellationToken ct)
    {
        foreach (var (path, hash) in release.Files!)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(destination, path), target);
            if (!hash.Equals(await HashFileAsync(target, ct), StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新中にエンジンが変更されました。再実行してください。");
        }
        return release;
    }

    private static async Task<InstalledComponent> DownloadComponentAsync(string staging, Component component, InstalledComponent release,
        HttpClient client, IProgress<string>? progress, CancellationToken ct)
    {
        var archive = Path.Combine(staging, component.Asset);
        using (var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
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
                if (mb != reported) { progress?.Report($"{component.Asset} を取得中 · {mb} MB"); reported = mb; }
            }
        }
        if (!release.Sha256.Equals(await HashFileAsync(archive, ct), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{component.Asset} の整合性確認に失敗しました。再実行してください。");
        var folder = Path.Combine(staging, component.LicenseFolder);
        Directory.CreateDirectory(folder);
        if (component.Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var name in component.Names)
            {
                var entry = zip.Entries.Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(Path.Combine(staging, name));
            }
            foreach (var entry in zip.Entries.Where(e => e.Name.Contains("license", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("copying", StringComparison.OrdinalIgnoreCase)))
                if (entry.Name.Length > 0) entry.ExtractToFile(Path.Combine(folder, Path.GetFileName(entry.Name)), true);
        }
        foreach (var licenseFile in component.Licenses)
        {
            var text = await client.GetStringAsync($"https://raw.githubusercontent.com/{component.Repository}/{Uri.EscapeDataString(release.Tag)}/{licenseFile}", ct);
            await File.WriteAllTextAsync(Path.Combine(folder, licenseFile), text, ct);
        }
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in component.Names.Concat(Directory.EnumerateFiles(folder).Select(path => component.LicenseFolder + "/" + Path.GetFileName(path))))
            files.Add(path, await HashFileAsync(Path.Combine(staging, path), ct));
        if (!HasExpectedFiles(component, files)) throw new IOException($"{component.Asset} のライセンス情報が不足しています。");
        return release with { Files = files };
    }

    internal static FileStream AcquireInstallLock(string destination)
    {
        try { return new FileStream(Path.Combine(destination, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException ex) { throw new IOException("別のエンジン更新が実行中です。完了してから再実行してください。", ex); }
    }

    internal static void PromoteStagedPackage(string staging, string destination)
    {
        string[] entries = [.. Executables, "licenses", "versions.json"];
        foreach (var name in entries)
            if (!Exists(Path.Combine(staging, name))) throw new IOException($"エンジンの配布ファイルが不足しています: {name}");

        var backup = Path.Combine(destination, ".backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var saved = new List<string>();
        var installed = new List<string>();
        var cleanupBackup = false;
        try
        {
            // Move every old entry out first. A running executable can block this step;
            // in that case restore the old set before any new executable is exposed.
            foreach (var name in entries)
            {
                if (!Exists(Path.Combine(destination, name))) continue;
                Move(destination, backup, name);
                saved.Add(name);
            }
            foreach (var name in entries)
            {
                Move(staging, destination, name);
                installed.Add(name);
            }
            cleanupBackup = true;
        }
        catch (Exception installError) when (installError is IOException or UnauthorizedAccessException)
        {
            try
            {
                foreach (var name in installed.AsEnumerable().Reverse()) Move(destination, staging, name);
                foreach (var name in saved.AsEnumerable().Reverse()) Move(backup, destination, name);
                cleanupBackup = true;
            }
            catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"更新と復元に失敗しました。以前のエンジンは {backup} に保持されています。", new AggregateException(installError, restoreError));
            }
            throw;
        }
        // A failed rollback must retain its backup. A completed rollback leaves it empty.
        finally
        {
            if (cleanupBackup)
                try { Directory.Delete(backup, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
        static void Move(string source, string target, string name)
        {
            var from = Path.Combine(source, name);
            var to = Path.Combine(target, name);
            if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
        }
    }

    private static async Task<JsonDocument> GetReleaseAsync(string repo, HttpClient client, CancellationToken ct)
    {
        try
        {
            return await client.GetFromJsonAsync<JsonDocument>($"https://api.github.com/repos/{repo}/releases/latest", ct)
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
