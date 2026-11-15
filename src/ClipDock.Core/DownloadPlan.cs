using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClipDock.Core;

public static class DownloadPlan
{
    // yt-dlp's default order (lang,quality,res,fps,hdr:12,vcodec,…,acodec,…) with only the codec preferences changed.
    public const string FormatSort = "lang,quality,res,fps,hdr:12,vcodec:h264,acodec:aac";

    public static ProcessStartInfo Create(DownloadRequest input, string tools, string output, string jobId, string? cookieFile = null)
    {
        var request = input.Validated();
        request.ValidateVideoPage();
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new ArgumentException("Invalid job identifier.");
        var start = new ProcessStartInfo(Path.Combine(tools, AppPaths.Executable("yt-dlp")))
        {
            // stdin is redirected so the caller can close it: the engine must never wait on (or inherit) a console or pipe.
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var quality = request.Quality == "best" ? "" : $"[height<=?{request.Quality}]";
        string[] args = ["--ignore-config", "--no-playlist", "--no-colors", "--newline", "--progress", "--no-simulate",
            "--encoding", "utf-8", "--windows-filenames", "--trim-filenames", "160", "--no-overwrites",
            "--socket-timeout", "30", "--retries", "3", "--fragment-retries", "3", "--abort-on-unavailable-fragments",
            "--ffmpeg-location", tools, "--js-runtimes", "deno:" + Path.Combine(tools, AppPaths.Executable("deno")),
            "--paths", Path.GetFullPath(output), "--paths", "temp:" + WorkDirectory(output, jobId), "--output", $"%(title).100B [%(id).50B]-{jobId[..8]}.%(ext)s",
            "--progress-template", "download:CLIPDOCK_PROGRESS %(progress)j",
            "--print", "before_dl:CLIPDOCK_TITLE %(title)j", "--print", "before_dl:CLIPDOCK_PARTS %(requested_formats.:.format_id)j",
            "--print", "after_move:CLIPDOCK_FILE %(filepath)j"];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        string[] formatArgs = request.Format == "mp3"
            ? ["--format", "ba/b", "--extract-audio", "--audio-format", "mp3", "--audio-quality", "0"]
            : ["--format", $"bv*{quality}+ba/b{quality}", "--format-sort", FormatSort,
                "--merge-output-format", "mp4", "--recode-video", "mp4",
                "--postprocessor-args", "VideoConvertor+ffmpeg_o:-c:v libx264 -preset fast -crf 20 -c:a aac -b:a 192k -movflags +faststart"];
        foreach (var arg in formatArgs) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--no-plugin-dirs");
        if (request.UseLogin && cookieFile is not null)
        {
            if (!Path.IsPathFullyQualified(cookieFile)) throw new ArgumentException("Invalid session file path.");
            start.ArgumentList.Add("--cookies"); start.ArgumentList.Add(cookieFile);
        }
        if (request.Referer.Length > 0) { start.ArgumentList.Add("--referer"); start.ArgumentList.Add(request.Referer); }
        if (request.UserAgent.Length > 0) { start.ArgumentList.Add("--user-agent"); start.ArgumentList.Add(request.UserAgent); }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(request.Url);
        return start;
    }

    public static string WorkDirectory(string output, string jobId)
    {
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new ArgumentException("Invalid job identifier.");
        return Path.Combine(Path.GetFullPath(output), ".clipdock-work", jobId);
    }

    public static void PrepareWorkDirectory(string output, string jobId)
    {
        var directory = WorkDirectory(output, jobId);
        foreach (var path in new[] { Path.GetDirectoryName(directory)!, directory })
        {
            var info = new DirectoryInfo(path);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("一時保存先を使用できません。");
            info.Create();
        }
        if (OperatingSystem.IsWindows()) File.SetAttributes(Path.GetDirectoryName(directory)!, File.GetAttributes(Path.GetDirectoryName(directory)!) | FileAttributes.Hidden);
    }

    public static void CleanWorkDirectory(string output, string jobId)
    {
        var directory = new DirectoryInfo(WorkDirectory(output, jobId));
        if (!directory.Exists) return;
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint) || directory.Parent!.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("一時保存先を削除できません。");
        directory.Delete(true);
    }

    public static bool IsOutputFile(string? file, string? directory, string format = "mp4")
    {
        // Both values can come from engine output or an old history file; anything malformed is simply "not ours".
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(directory) || format is not ("mp4" or "mp3")) return false;
        try
        {
            return Path.GetExtension(file).Equals("." + format, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(file).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
}

public sealed record DownloadProgress(double Percent, double BytesPerSecond, double? SecondsRemaining, bool Finished, string FileName = "")
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
            var filename = root.TryGetProperty("filename", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
            return new DownloadProgress(total > 0 ? Math.Clamp(Number("downloaded_bytes") / total * 100, 0, 100) : 0,
                Number("speed"), Number("eta") > 0 ? Number("eta") : null, done, filename);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

public sealed class DownloadProgressTracker
{
    private readonly HashSet<string> finished = [];
    private int partCount = 1;
    private int unnamedPart;
    private bool lastFinished;
    private double highest;

    public void SetParts(string json)
    {
        try
        {
            using var value = JsonDocument.Parse(json);
            if (value.RootElement.ValueKind == JsonValueKind.Array) partCount = Math.Clamp(value.RootElement.GetArrayLength(), 1, 16);
        }
        catch (JsonException) { }
    }
    public DownloadProgress Apply(DownloadProgress progress)
    {
        if (!progress.Finished && lastFinished && progress.FileName.Length == 0) unnamedPart++;
        var key = progress.FileName.Length > 0 ? progress.FileName : unnamedPart.ToString();
        if (progress.Finished) finished.Add(key);
        var fraction = finished.Count + (finished.Contains(key) ? 0 : progress.Percent / 100);
        highest = Math.Max(highest, Math.Clamp(fraction / partCount * 95, 0, 95));
        lastFinished = progress.Finished;
        return progress with { Percent = highest, Finished = finished.Count >= partCount, SecondsRemaining = null };
    }
}
