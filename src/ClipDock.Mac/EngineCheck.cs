using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ClipDock.App;
using ClipDock.Core;

namespace ClipDock.Mac;

// Real-engine check without a window: installs the engine, then saves synthetic media through the same
// code path the app uses. Only generated test media and a loopback server are involved.
internal static class EngineCheck
{
    internal static async Task<int> RunAsync(string directory)
    {
        try
        {
            var tools = Path.Combine(directory, "tools");
            var progress = new InlineProgress();
            using var client = CreateClient();
            await ToolInstaller.InstallWithClientAsync(tools, client, progress);
            if (!ToolInstaller.IsReady(tools)) throw new IOException("The engine is not ready after installation.");
            progress.Messages.Clear();
            await ToolInstaller.InstallWithClientAsync(tools, client, progress);
            if (!progress.Messages.Any(message => message.Contains("再ダウンロードは不要"))) throw new IOException("An unchanged engine was downloaded again.");
            Console.WriteLine("PASS engine installed, verified and reused without a second download");

            foreach (var (name, argument) in new[] { ("yt-dlp", "--version"), ("ffmpeg", "-version"), ("ffprobe", "-version"), ("deno", "--version") })
                Console.WriteLine($"PASS {name}: {(await RunAsync(Path.Combine(tools, AppPaths.Executable(name)), directory, argument)).Split('\n')[0].Trim()}");

            var fixture = Path.Combine(directory, "fixture");
            Directory.CreateDirectory(fixture);
            var sample = Path.Combine(fixture, "sample.mp4");
            await RunAsync(Path.Combine(tools, AppPaths.Executable("ffmpeg")), fixture, "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", sample);

            var port = FreePort();
            using var server = new HttpListener();
            server.Prefixes.Add($"http://127.0.0.1:{port}/");
            server.Start();
            _ = ServeAsync(server, sample);
            var output = Path.Combine(directory, "保存先 with spaces");
            foreach (var format in new[] { "mp4", "mp3" })
            {
                var url = $"http://127.0.0.1:{port}/sample.mp4";
                var item = new DownloadItem
                {
                    Request = new DownloadRequest { Url = url, PageUrl = url, Quality = "720", Format = format, UseLogin = false }.Validated(),
                    OutputDirectory = output
                };
                await DownloadEngine.RunAsync(item, tools, action => action(), CancellationToken.None);
                if (item.State != "completed" || !File.Exists(item.FilePath) || new FileInfo(item.FilePath).Length == 0)
                    throw new IOException($"{format} was not saved: {item.State} {item.Detail}");
                Console.WriteLine($"PASS saved {format}: {Path.GetFileName(item.FilePath)} ({new FileInfo(item.FilePath).Length} bytes)");
            }
            server.Stop();
            Console.WriteLine("Engine checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            return 1;
        }
    }

    // Shared build machines exhaust GitHub's anonymous request limit; a token from the environment is sent to api.github.com only.
    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new ApiToken(Environment.GetEnvironmentVariable("GITHUB_TOKEN"))) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipDock-engine-check");
        return client;
    }

    private sealed class ApiToken(string? token) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (token is { Length: > 0 } && request.RequestUri is { Scheme: "https", Host: "api.github.com" })
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class InlineProgress : IProgress<string>
    {
        internal List<string> Messages { get; } = [];
        public void Report(string value)
        {
            lock (Messages) Messages.Add(value);
            if (!value.Contains(" MB")) Console.WriteLine(value);
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task ServeAsync(HttpListener server, string file)
    {
        var data = await File.ReadAllBytesAsync(file);
        while (server.IsListening)
        {
            HttpListenerContext context;
            try { context = await server.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try
            {
                context.Response.ContentType = "video/mp4";
                context.Response.ContentLength64 = data.Length;
                if (context.Request.HttpMethod != "HEAD") await context.Response.OutputStream.WriteAsync(data);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> RunAsync(string program, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + program);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(limit.Token);
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(program)} exited with {process.ExitCode}: {await errors}");
        return await output;
    }
}
