using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClipDock.Core;

// A newer ClipDock release for this computer, as announced by the official site.
public sealed record UpdateOffer(Version Version, Uri Url, string Sha256, long Size)
{
    public string FileName => Url.Segments[^1];
}

// Self-update of the app. The official site publishes /updates/latest.json next to the downloads; it is
// trusted over HTTPS, and a downloaded file is used only when its size and SHA-256 match that manifest.
public static class AppUpdate
{
    public static readonly Uri ManifestUrl = new("https://clipdock-d6z.pages.dev/updates/latest.json");
    private const long MaximumSize = 512L * 1024 * 1024;

    // Static initializers run in source order: Current must be set before the client that names it in its User-Agent.
    // (1.0.5 had them the other way round, so the first update check failed while initializing this class.)
    public static Version Current { get; } = Normalize(typeof(AppUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0));
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipDock/{Current.ToString(3)}");
        return client;
    }

    // The User-Agent the update check sends; reading it also initializes everything the real check uses.
    internal static string UserAgent => Client.DefaultRequestHeaders.UserAgent.ToString();

    // The manifest key for this build: win-x64, mac-arm64 or mac-x64 (Rosetta processes take the Intel build).
    public static string Platform => OperatingSystem.IsWindows() ? "win-x64"
        : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mac-arm64" : "mac-x64";

    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    // Null when the manifest offers nothing newer for this platform. Anything malformed or pointing away from
    // the site's own /downloads/ route is rejected rather than trusted.
    public static UpdateOffer? Parse(string json, Version current, string platform, Uri manifest)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var versionText) ||
            versionText.ValueKind != JsonValueKind.String || !TryParseVersion(versionText.GetString(), out var version))
            throw new InvalidDataException("The update information has no valid version.");
        if (version <= Normalize(current)) return null;
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object ||
            !files.TryGetProperty(platform, out var file) || file.ValueKind != JsonValueKind.Object) return null;
        string? Text(string key) => file.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var sha256 = Text("sha256")?.ToLowerInvariant();
        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The update information has no valid SHA-256.");
        if (!file.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number ||
            !sizeValue.TryGetInt64(out var size) || size <= 0 || size > MaximumSize)
            throw new InvalidDataException("The update information has no valid size.");
        if (Text("url") is not { } link || !Uri.TryCreate(manifest, link, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host != manifest.Host ||
            !url.AbsolutePath.StartsWith($"/downloads/ClipDock-{version.ToString(3)}-", StringComparison.Ordinal) || url.Query.Length > 0)
            throw new InvalidDataException("The update download is not on the official site.");
        return new UpdateOffer(version, url, sha256, size);
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var parts = text?.Split('.');
        if (parts is not { Length: 3 } || parts.Any(p => p.Length is 0 or > 5 || !p.All(char.IsAsciiDigit))) return false;
        version = new Version(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        return true;
    }

    // Once a version is installed, its installer (or disk image) and anything older or unfinished is only taking space.
    // A verified download of a newer version is kept, so it is not fetched again after a restart. Files still in use,
    // such as an installer that is just finishing, stay until the next call.
    public static void RemoveInstalled(string directory, Version current)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            var newer = name.StartsWith("ClipDock-", StringComparison.Ordinal) && !name.EndsWith(".partial", StringComparison.Ordinal) &&
                TryParseVersion(name.Split('-')[1], out var version) && version > Normalize(current);
            if (newer) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static Task<UpdateOffer?> CheckAsync(CancellationToken ct = default) => CheckWithClientAsync(Client, ManifestUrl, Current, Platform, ct);

    internal static async Task<UpdateOffer?> CheckWithClientAsync(HttpClient client, Uri manifest, Version current, string platform, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct), current, platform, manifest);
    }

    public static Task<string> DownloadAsync(UpdateOffer offer, string directory, CancellationToken ct = default) => DownloadWithClientAsync(Client, offer, directory, ct);

    // Returns the verified file in directory, reusing an earlier download of the same release. Other files there
    // (older releases, interrupted downloads) are removed.
    internal static async Task<string> DownloadWithClientAsync(HttpClient client, UpdateOffer offer, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, offer.FileName);
        foreach (var stale in Directory.EnumerateFiles(directory).Where(path => !string.Equals(path, target, StringComparison.OrdinalIgnoreCase)))
            try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (File.Exists(target))
        {
            if (await MatchesAsync(target, offer, ct)) return target;
            File.Delete(target);
        }
        var partial = target + ".partial";
        try
        {
            using (var response = await client.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(partial);
                var buffer = new byte[128 * 1024];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    received += count;
                    if (received > offer.Size) throw new InvalidDataException("The update download is larger than announced.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            if (!await MatchesAsync(partial, offer, ct)) throw new InvalidDataException("The update download does not match its SHA-256.");
            File.Move(partial, target, true);
            return target;
        }
        finally { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static async Task<bool> MatchesAsync(string path, UpdateOffer offer, CancellationToken ct)
    {
        if (new FileInfo(path).Length != offer.Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
