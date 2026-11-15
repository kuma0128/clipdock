using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ClipDock.Core;

internal static class StorePolicyChecks
{
    private static void Assert(bool value, string? message = null) { if (!value) throw new Exception(message ?? "Assertion failed"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Expected a rejected store request");
    }

    public static void Run(Action<string, Action> check)
    {
        check("store requests reject YouTube hosts in the target, page and referer", () =>
        {
            foreach (var host in new[] { "youtube.com", "m.youtube.com", "YOUTUBE.COM.", "youtu.be", "www.youtube-nocookie.com",
                "rr1.googlevideo.com", "i.ytimg.com", "youtube.googleapis.com", "youtubei.googleapis.com", "ｙｏｕｔｕｂｅ.com" })
            {
                var url = "https://" + host + "/video";
                var request = new DownloadRequest { RestrictYouTube = true, Url = "https://example.com/media.mp4" };
                Reject(() => (request with { Url = url }).Validated());
                Reject(() => (request with { PageUrl = url }).Validated());
                Reject(() => (request with { Referer = url }).Validated());
                Reject(() => (request with { PageUrl = "blob:" + url }).Validated());
            }
        });
        check("store host restrictions preserve unrelated hosts and local requests", () =>
        {
            foreach (var url in new[] { "https://youtube.com.example.org/v", "https://notyoutube.com/v", "https://example.com/youtube.com", "https://x.com/a/status/123" })
                Assert(new DownloadRequest { RestrictYouTube = true, Url = url }.Validated().Url == url);
            Assert(new DownloadRequest { Url = "https://youtube.com/watch?v=local" }.Validated().Url.Contains("youtube.com"));
        });
        check("native JSON preserves store restrictions and older requests default to local", () =>
        {
            var request = JsonSerializer.Deserialize<DownloadRequest>("{\"url\":\"https://example.com/v\",\"restrictYouTube\":true}", Json.Options)!;
            Assert(request.Validated().RestrictYouTube);
            Assert(JsonSerializer.Serialize(request, Json.Options).Contains("\"restrictYouTube\": true"));
            Assert(!JsonSerializer.Deserialize<DownloadRequest>("{\"url\":\"https://example.com/v\"}", Json.Options)!.RestrictYouTube);
        });
        check("only store engine plans disable every YouTube extractor and external plugins", () =>
        {
            foreach (var restricted in new[] { false, true })
            {
                var args = DownloadPlan.Create(new DownloadRequest { RestrictYouTube = restricted, Url = "https://example.com/v" },
                    @"C:\tools", @"C:\videos", new string('a', 32), null).ArgumentList;
                Assert(args.Contains("--use-extractors") == restricted && args.Contains("--no-plugin-dirs") == restricted);
                if (restricted) Assert(args[args.IndexOf("--use-extractors") + 1] == "default,-.*youtube.*");
                Assert(args[^2] == "--" && args[^1] == "https://example.com/v");
            }
        });
        check("store cookie requests reject HTTP targets, pages and referers", () =>
        {
            var request = new DownloadRequest { RestrictYouTube = true, Url = "https://example.com/v", Cookies =
                [new BrowserCookie { Domain = "example.com", Name = "session", Value = "synthetic" }] };
            foreach (var insecure in new[] { request with { Url = "http://example.com/v" },
                request with { PageUrl = "http://example.com/" }, request with { Referer = "http://example.com/" } })
            {
                Reject(() => insecure.Validated());
                Reject(() => DownloadPlan.CookieFile(insecure));
                Reject(() => DownloadPlan.Create(insecure with { Cookies = [] }, @"C:\tools", @"C:\videos", new string('a', 32), "cookies"));
                Assert((insecure with { RestrictYouTube = false }).Validated().Cookies.Count == 1);
                Assert((insecure with { Cookies = [] }).Validated().Cookies.Count == 0);
            }
        });
        check("store cookie jars mark imported cookies Secure without changing local jars", () =>
        {
            var cookie = new BrowserCookie { Domain = "example.com", HostOnly = true, Name = "session", Value = "synthetic" };
            var request = new DownloadRequest { RestrictYouTube = true, Url = "https://example.com/v", Cookies = [cookie] };
            Assert(request.Validated().Cookies.Single().Secure && !cookie.Secure);
            Assert(DownloadPlan.CookieFile(request).Contains("example.com\tFALSE\t/\tTRUE\t0\tsession\tsynthetic"));
            Assert(DownloadPlan.CookieFile(request with { RestrictYouTube = false }).Contains("example.com\tFALSE\t/\tFALSE\t0\tsession\tsynthetic"));
        });
        check("only cookie-bearing store plans restrict FFmpeg input protocols", () =>
        {
            foreach (var restricted in new[] { false, true })
            foreach (var cookieFile in new string?[] { null, "cookies" })
            {
                var args = DownloadPlan.Create(new DownloadRequest { RestrictYouTube = restricted, Url = "https://example.com/v" },
                    @"C:\tools", @"C:\videos", new string('a', 32), cookieFile).ArgumentList;
                Assert(args.Contains("--downloader-args") == (restricted && cookieFile is not null));
                if (restricted && cookieFile is not null)
                    Assert(args[args.IndexOf("--downloader-args") + 1] == "ffmpeg_i:-protocol_whitelist https,tls,tcp,crypto,data");
            }
        });
    }

    public static async Task<int> RunEngineAsync(string tools)
    {
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var serving = ServeAsync(listener, stop.Token);
        try
        {
            foreach (var path in new[] { "/iframe", "/videojs", "/redirect" })
            {
                var result = await ExtractAsync(tools, origin + path);
                Assert(result.Code != 0 && (result.Log.Contains("Unsupported URL:") || result.Log.Contains("No suitable extractor")));
                Assert(!result.Log.Contains("[youtube]", StringComparison.OrdinalIgnoreCase));
                if (path == "/redirect") Assert(result.Log.Contains("Following redirect to https://www.youtube.com/"));
                Console.WriteLine("PASS store engine rejects " + path + " without a YouTube extractor");
            }
            var media = await ExtractAsync(tools, origin + "/sample.mp4");
            Assert(media.Code == 0 && media.Log.Contains("generic"));
            Console.WriteLine("PASS store engine retains generic direct-media extraction");
            await CheckCookieTransportAsync(tools);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            stop.Cancel(); listener.Stop();
            try { await serving; } catch (OperationCanceledException) { } catch (SocketException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task CheckCookieTransportAsync(string tools)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClipDock-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sampleFile = Path.Combine(directory, "sample.mp4");
            var generate = new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=5",
                "-t", "0.4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", sampleFile }) generate.ArgumentList.Add(arg);
            var generated = await ExecuteAsync(generate);
            Assert(generated.Code == 0);
            await using var fixture = new CookieTransportServer(await File.ReadAllBytesAsync(sampleFile));

            async Task<(int Code, string Log)> Run(string name, bool ffmpeg, string protocol = "https")
            {
                var request = new DownloadRequest { RestrictYouTube = true, Url = fixture.HttpsOrigin + "/" + name,
                    Cookies = [new BrowserCookie { Domain = "127.0.0.1", HostOnly = true, Name = "clipdock_test", Value = "synthetic-only" }] };
                var cookieFile = Path.Combine(directory, name + ".cookies");
                await File.WriteAllTextAsync(cookieFile, DownloadPlan.CookieFile(request));
                var start = DownloadPlan.Create(request, tools, directory, Guid.NewGuid().ToString("N"), cookieFile);
                start.ArgumentList[start.ArgumentList.IndexOf("--retries") + 1] = "0";
                start.ArgumentList[start.ArgumentList.IndexOf("--fragment-retries") + 1] = "0";
                start.ArgumentList[start.ArgumentList.IndexOf("--socket-timeout") + 1] = "5";
                var extra = new List<string> { "--no-check-certificates", "--no-cache-dir", "--no-quiet" };
                if (ffmpeg)
                {
                    start.ArgumentList[start.ArgumentList.IndexOf("--downloader-args") + 1] += " -tls_verify 0";
                    var info = Path.Combine(directory, name + ".json");
                    await File.WriteAllTextAsync(info, JsonSerializer.Serialize(new { id = name, title = name, url = request.Url, ext = "mp4", protocol }));
                    start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
                    start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
                    extra.AddRange(["--downloader", "ffmpeg", "--load-info-json", info]);
                    foreach (var arg in extra) start.ArgumentList.Add(arg);
                }
                else foreach (var arg in extra) start.ArgumentList.Insert(start.ArgumentList.Count - 2, arg);
                var result = await ExecuteAsync(start);
                return result;
            }

            var native = await Run("native-redirect.mp4", false);
            Assert(native.Code == 0, native.Log);
            Assert(fixture.Requests.Any(r => r.Path == "/native-redirect.mp4" && r.Secure && r.HasCookie));
            Assert(fixture.Requests.Any(r => r.Path == "/native-target.mp4" && !r.Secure));
            Assert(!fixture.Requests.Any(r => !r.Secure && r.HasCookie));
            Console.WriteLine("PASS native HTTPS to HTTP redirect omits the imported cookie");

            var secure = await Run("secure.mp4", true);
            Assert(secure.Code == 0 && fixture.Requests.Any(r => r.Path == "/secure.mp4" && r.Secure && r.HasCookie), secure.Log);
            Console.WriteLine("PASS FFmpeg HTTPS download receives the imported cookie");

            var redirect = await Run("ffmpeg-redirect.mp4", true);
            Assert(redirect.Code != 0 && redirect.Log.Contains("Protocol 'http' not on whitelist"), redirect.Log);
            Assert(fixture.Requests.Any(r => r.Path == "/ffmpeg-redirect.mp4" && r.Secure && r.HasCookie));
            Assert(!fixture.Requests.Any(r => r.Path == "/ffmpeg-target.mp4"));
            Console.WriteLine("PASS FFmpeg rejects the HTTP redirect before requesting its target");

            var hls = await Run("playlist.m3u8", true, "m3u8");
            Assert(hls.Code != 0 && hls.Log.Contains("not on whitelist"), hls.Log);
            Assert(fixture.Requests.Any(r => r.Path == "/playlist.m3u8" && r.Secure && r.HasCookie));
            Assert(!fixture.Requests.Any(r => r.Path == "/segment.ts"));
            Console.WriteLine("PASS FFmpeg rejects the HTTP HLS segment before sending a request");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<(int Code, string Log)> ExecuteAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        if (start.RedirectStandardInput) process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await stdout + await stderr);
    }

    private sealed class CookieTransportServer : IAsyncDisposable
    {
        private readonly TcpListener http = new(IPAddress.Loopback, 0);
        private readonly TcpListener https = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly X509Certificate2 certificate;
        private readonly byte[] media;
        private readonly Task serving;
        public readonly ConcurrentBag<(string Path, bool Secure, bool HasCookie)> Requests = [];
        public string HttpsOrigin { get; }
        private string HttpOrigin { get; }

        public CookieTransportServer(byte[] media)
        {
            this.media = media;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            http.Start(); https.Start();
            HttpOrigin = $"http://127.0.0.1:{((IPEndPoint)http.LocalEndpoint).Port}";
            HttpsOrigin = $"https://127.0.0.1:{((IPEndPoint)https.LocalEndpoint).Port}";
            serving = Task.WhenAll(Serve(http, false), Serve(https, true));
        }

        private async Task Serve(TcpListener listener, bool secure)
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await using var network = client.GetStream();
                using var tls = secure ? new SslStream(network, true) : null;
                if (tls is not null) await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 }, stop.Token);
                var stream = (Stream?)tls ?? network;
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var request = await reader.ReadLineAsync(stop.Token) ?? "";
                var headers = new List<string>();
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line) headers.Add(line);
                var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                Requests.Add((path, secure, headers.Any(h => h.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) && h.Contains("clipdock_test=synthetic-only"))));
                var target = path switch { "/native-redirect.mp4" => "/native-target.mp4", "/ffmpeg-redirect.mp4" => "/ffmpeg-target.mp4", _ => null };
                var body = path == "/playlist.m3u8"
                    ? Encoding.UTF8.GetBytes($"#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:1,\n{HttpOrigin}/segment.ts\n#EXT-X-ENDLIST\n") : media;
                var contentType = path == "/playlist.m3u8" ? "application/vnd.apple.mpegurl" : "video/mp4";
                var response = target is null ? "HTTP/1.1 200 OK\r\n" : $"HTTP/1.1 302 Found\r\nLocation: {HttpOrigin}{target}\r\n";
                response += $"Content-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                await stream.WriteAsync(body, stop.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); http.Stop(); https.Stop();
            try { await serving; } catch (OperationCanceledException) { } catch (SocketException) when (stop.IsCancellationRequested) { }
            certificate.Dispose(); stop.Dispose();
        }
    }

    private static async Task<(int Code, string Log)> ExtractAsync(string tools, string url)
    {
        var start = DownloadPlan.Create(new DownloadRequest { RestrictYouTube = true, Url = url }, tools,
            Path.GetTempPath(), Guid.NewGuid().ToString("N"), null);
        start.ArgumentList[start.ArgumentList.IndexOf("--no-simulate")] = "--simulate";
        start.ArgumentList[start.ArgumentList.IndexOf("--socket-timeout") + 1] = "5";
        start.ArgumentList[start.ArgumentList.IndexOf("--retries") + 1] = "0";
        foreach (var arg in new[] { "--no-quiet", "--no-cache-dir", "--extractor-retries", "0", "--print", "%(extractor)s" })
            start.ArgumentList.Insert(start.ArgumentList.Count - 2, arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        var log = await stdout + await stderr;
        Console.WriteLine(log.TrimEnd());
        return (process.ExitCode, log);
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            var request = await reader.ReadLineAsync(ct) ?? "";
            while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
            var path = request.Split(' ').ElementAtOrDefault(1);
            var body = path switch
            {
                "/iframe" => "<html><title>ClipDock test</title><iframe src=\"https://www.youtube.com/embed/BaW_jenozKc\"></iframe></html>",
                "/videojs" => "<html><title>ClipDock test</title><script>var player = videojs('test'); player.src({type: 'video/youtube', src: 'https://www.youtube.com/watch?v=BaW_jenozKc'});</script></html>",
                _ => "ClipDock synthetic extraction fixture"
            };
            var bytes = Encoding.UTF8.GetBytes(body);
            var headers = path == "/redirect"
                ? "HTTP/1.1 302 Found\r\nLocation: https://www.youtube.com/watch?v=BaW_jenozKc\r\n"
                : "HTTP/1.1 200 OK\r\n";
            headers += $"Content-Type: {(path == "/sample.mp4" ? "video/mp4" : "text/html")}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
            await stream.WriteAsync(bytes, ct);
        }
    }
}
