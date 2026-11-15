using System.Buffers.Binary;
using System.Text;
using ClipDock.Core;

var passed = 0;
var failed = 0;
void Check(string name, Action check)
{
    try { check(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

Check("only absolute HTTP(S) URLs are accepted", () =>
{
    foreach (var url in new[] { "file:///C:/secret", "javascript:alert(1)", "ftp://a.com/file", "--exec=calc", "https://u:p@example.com/v", "https://example.com/\n--exec", "", "https://" })
        Assert(!DownloadRequest.IsWebUrl(url));
    Assert(DownloadRequest.IsWebUrl("https://example.com/video?id=a&token=b"));
});
Check("unknown actions and formats cannot become downloader arguments", () =>
{
    Throws<ArgumentException>(() => new DownloadRequest { Action = "exec" }.Validated());
    Throws<ArgumentException>(() => new DownloadRequest { Url = "https://example.com", Quality = "1080 --exec calc" }.Validated());
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
Check("cookies are restricted to requested domains and cannot inject cookie lines", () =>
{
    var r = new DownloadRequest { Url = "https://media.example.com/v", Cookies = [
        new() { Domain = ".example.com", Name = "session", Value = "ok", HttpOnly = true, Secure = true },
        new() { Domain = "evil-example.com", Name = "session", Value = "bad" },
        new() { Domain = "example.com", HostOnly = true, Name = "session", Value = "bad" },
        new() { Domain = ".example.com", Name = "broken", Value = "a\nb" },
        new() { Domain = "com", Name = "broad", Value = "bad" },
    ] }.Validated();
    Assert(r.Cookies.Count == 1);
    Assert(DownloadPlan.CookieFile(r).Contains("#HttpOnly_.example.com\tTRUE\t/\tTRUE\t0\tsession\tok"));
});
Check("null and excessive cookie payloads are rejected", () =>
{
    Throws<ArgumentException>(() => new DownloadRequest { Url = "https://a.test", Cookies = null! }.Validated());
    Throws<ArgumentException>(() => new DownloadRequest { Url = "https://a.test", Cookies = Enumerable.Range(0, 501).Select(_ => new BrowserCookie()).ToList() }.Validated());
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
Check("CLI uses discrete arguments and URL terminator, ignores external configs", () =>
{
    var url = "https://example.com/v?x=\" & calc & echo \"";
    var start = DownloadPlan.Create(new DownloadRequest { Url = url, Title = "--exec calc" }, @"C:\tool space", @"C:\video space", new string('a', 32), @"C:\session space\cookie");
    Assert(!start.UseShellExecute && start.CreateNoWindow);
    Assert(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
    Assert(start.ArgumentList[^2] == "--" && start.ArgumentList[^1] == url);
    Assert(start.ArgumentList.Contains("--ignore-config") && start.ArgumentList.Contains("--no-playlist"));
    Assert(start.ArgumentList.Contains("bv*[height<=?1080]+ba/b[height<=?1080]"));
    Assert(start.ArgumentList.Contains("--recode-video") && start.ArgumentList.Contains("--abort-on-unavailable-fragments"));
    Assert(start.ArgumentList.Contains(@"C:\session space\cookie"));
    Assert(!start.ArgumentList.Contains("--exec"));
});
Check("best quality omits height cap; all specified qualities are bounded", () =>
{
    foreach (var quality in new[] { "best", "480", "720", "1080", "2160" })
    {
        var args = DownloadPlan.Create(new DownloadRequest { Url = "https://a.test", Quality = quality }, @"C:\tools", @"C:\videos", new string('b', 32), null).ArgumentList;
        Assert(args.Contains(quality == "best" ? "bv*+ba/b" : $"bv*[height<=?{quality}]+ba/b[height<=?{quality}]"));
    }
});
Check("format sort ranks resolution above the H.264/AAC preference", () =>
{
    var args = DownloadPlan.Create(new DownloadRequest { Url = "https://a.test", Quality = "2160" }, @"C:\tools", @"C:\videos", new string('c', 32), null).ArgumentList;
    var fields = args[args.IndexOf("--format-sort") + 1].Split(',').ToList();
    Assert(fields.IndexOf("res") >= 0 && fields.IndexOf("res") < fields.IndexOf("vcodec:h264") && fields.IndexOf("vcodec:h264") < fields.IndexOf("acodec:aac"));
});
Check("output verification tolerates missing or malformed paths", () =>
{
    Assert(!DownloadPlan.IsOutputFile(null, @"C:\videos") && !DownloadPlan.IsOutputFile("", @"C:\videos"));
    Assert(!DownloadPlan.IsOutputFile(@"C:\videos\clip.mp4", null) && !DownloadPlan.IsOutputFile(@"C:\videos\clip.mp4", "  "));
    Assert(!DownloadPlan.IsOutputFile("C:\\videos\\cl\0ip.mp4", @"C:\videos"));
    Assert(DownloadPlan.IsOutputFile(@"D:\clip.MP4", @"D:\"));
});
Check("output verification rejects sibling path escapes and non-MP4", () =>
{
    Assert(DownloadPlan.IsOutputFile(@"C:\videos\clip.mp4", @"C:\videos"));
    Assert(!DownloadPlan.IsOutputFile(@"C:\videos-other\clip.mp4", @"C:\videos"));
    Assert(!DownloadPlan.IsOutputFile(@"C:\videos\..\secret.mp4", @"C:\videos"));
    Assert(!DownloadPlan.IsOutputFile(@"C:\videos\app.exe", @"C:\videos"));
});
Check("native messages handle fragmented UTF-8 reads", () =>
{
    using var stream = new MemoryStream();
    var request = new DownloadRequest { Url = "https://example.com", Title = "日本語の動画 🎬" };
    NativeProtocol.WriteAsync(stream, request).GetAwaiter().GetResult();
    var data = stream.ToArray();
    Assert(BinaryPrimitives.ReadInt32LittleEndian(data) == data.Length - 4);
    using var fragmented = new FragmentedStream(data);
    var decoded = NativeProtocol.ReadAsync<DownloadRequest>(fragmented).GetAwaiter().GetResult();
    Assert(decoded.Title == request.Title);
});
Check("native messages reject negative and oversized lengths before allocation", () =>
{
    foreach (var length in new[] { -1, 0, NativeProtocol.MaxMessageBytes + 1, int.MaxValue })
    {
        var data = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(data, length);
        Throws<InvalidDataException>(() => NativeProtocol.ReadAsync<DownloadRequest>(new MemoryStream(data)).GetAwaiter().GetResult());
    }
});
Check("native messages reject truncated payloads", () =>
{
    Throws<EndOfStreamException>(() => NativeProtocol.ReadAsync<DownloadRequest>(new MemoryStream([5, 0, 0, 0, 123])).GetAwaiter().GetResult());
});
Check("DPAPI encrypts data at rest and rejects tampering", () =>
{
    var bytes = Encoding.UTF8.GetBytes("signed-url-and-private-cookie");
    var encrypted = WindowsProtection.Protect(bytes);
    Assert(!encrypted.AsSpan().SequenceEqual(bytes));
    Assert(WindowsProtection.Unprotect(encrypted).AsSpan().SequenceEqual(bytes));
    encrypted[^1] ^= 0x20;
    Throws<System.ComponentModel.Win32Exception>(() => WindowsProtection.Unprotect(encrypted));
});

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
