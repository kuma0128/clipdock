using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClipDock.Core;

public static class DownloadPlan
{
    // yt-dlp's default order (lang,quality,res,fps,hdr:12,vcodec,…,acodec,…) with only the codec preferences changed.
    public const string FormatSort = "lang,quality,res,fps,hdr:12,vcodec:h264,acodec:aac";

    public static ProcessStartInfo Create(DownloadRequest input, string tools, string output, string jobId, string? cookieFile)
    {
        var request = input.Validated();
        if (request.Action != "download") throw new ArgumentException("ダウンロード操作ではありません。");
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new ArgumentException("Invalid job identifier.");
        var start = new ProcessStartInfo(Path.Combine(tools, "yt-dlp.exe"))
        {
            // stdin is redirected so the caller can close it: the engine must never wait on (or inherit) a console or pipe.
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var quality = request.Quality == "best" ? "" : $"[height<=?{request.Quality}]";
        string[] args = ["--ignore-config", "--no-playlist", "--no-colors", "--newline", "--progress", "--no-simulate",
            "--encoding", "utf-8", "--windows-filenames", "--trim-filenames", "160", "--no-overwrites",
            "--socket-timeout", "30", "--retries", "3", "--fragment-retries", "3", "--abort-on-unavailable-fragments",
            "--ffmpeg-location", tools, "--js-runtimes", "deno:" + Path.Combine(tools, "deno.exe"),
            // Resolution first, codec second: with vcodec ahead of res, sites that serve H.264 only up to 1080p
            // (YouTube) would never reach the 2160p / best choices. H.264/AAC still wins at equal resolution.
            "--format", $"bv*{quality}+ba/b{quality}", "--format-sort", FormatSort,
            "--merge-output-format", "mp4", "--recode-video", "mp4",
            "--postprocessor-args", "VideoConvertor+ffmpeg_o:-c:v libx264 -preset fast -crf 20 -c:a aac -b:a 192k -movflags +faststart",
            "--paths", Path.GetFullPath(output), "--output", $"%(title).100B [%(id).50B]-{jobId[..8]}.%(ext)s",
            "--progress-template", "download:CLIPDOCK_PROGRESS %(progress)j",
            "--print", "before_dl:CLIPDOCK_TITLE %(title)j", "--print", "after_move:CLIPDOCK_FILE %(filepath)j"];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (request.RestrictYouTube)
        {
            start.ArgumentList.Add("--no-plugin-dirs");
            start.ArgumentList.Add("--use-extractors");
            start.ArgumentList.Add("default,-.*youtube.*");
        }
        if (request.Referer.Length > 0) { start.ArgumentList.Add("--referer"); start.ArgumentList.Add(request.Referer); }
        if (request.UserAgent.Length > 0) { start.ArgumentList.Add("--user-agent"); start.ArgumentList.Add(request.UserAgent); }
        if (cookieFile is not null)
        {
            request.ValidateCookieTransport();
            if (request.RestrictYouTube)
            {
                start.ArgumentList.Add("--downloader-args");
                start.ArgumentList.Add("ffmpeg_i:-protocol_whitelist https,tls,tcp,crypto,data");
            }
            start.ArgumentList.Add("--cookies"); start.ArgumentList.Add(cookieFile);
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(request.Url);
        return start;
    }

    public static string CookieFile(DownloadRequest request)
    {
        var result = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in request.Validated().Cookies)
        {
            result.Append(c.HttpOnly ? "#HttpOnly_" : "").Append(c.Domain).Append('\t')
                .Append(c.HostOnly ? "FALSE" : "TRUE").Append('\t').Append(c.Path).Append('\t')
                .Append(c.Secure ? "TRUE" : "FALSE").Append('\t')
                .Append(((long)(c.ExpirationDate ?? 0)).ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(c.Name).Append('\t').Append(c.Value).Append('\n');
        }
        return result.ToString();
    }

    public static bool IsOutputFile(string? file, string? directory)
    {
        // Both values can come from engine output or an old history file; anything malformed is simply "not ours".
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            return Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(file).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
}

public sealed record DownloadProgress(double Percent, double BytesPerSecond, double? SecondsRemaining, bool Finished)
{
    public static DownloadProgress? Parse(string line)
    {
        const string prefix = "CLIPDOCK_PROGRESS ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        try
        {
            using var json = JsonDocument.Parse(line[prefix.Length..]);
            var root = json.RootElement;
            double Number(string key) => root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : 0;
            var total = Number("total_bytes");
            if (total <= 0) total = Number("total_bytes_estimate");
            var done = root.TryGetProperty("status", out var status) && status.GetString() == "finished";
            return new DownloadProgress(total > 0 ? Math.Clamp(Number("downloaded_bytes") / total * 100, 0, 100) : 0,
                Number("speed"), Number("eta") > 0 ? Number("eta") : null, done);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
