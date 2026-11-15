using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipDock.Core;

namespace ClipDock.App;

public static partial class DownloadEngine
{
    [GeneratedRegex(@"https?://[^\s""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    public static async Task RunAsync(DownloadItem item, string tools, Action<Action> dispatch, CancellationToken ct,
        Func<DownloadRequest, CancellationToken, Task<SessionCookieFile?>>? sessionProvider = null)
    {
        var errors = new Queue<string>();
        string? resultPath = null;
        Action? finish = null;
        SessionCookieFile? session = null;
        var progressTracker = new DownloadProgressTracker();
        try
        {
            dispatch(() => { item.State = "downloading"; item.Detail = ClipDock.Core.Localization.Text("動画情報を解析しています…"); });
            ct.ThrowIfCancellationRequested();
            var request = item.Request;
            request.ValidateVideoPage();
            Directory.CreateDirectory(item.OutputDirectory);
            if (!ToolInstaller.IsReady(tools)) throw new IOException(ClipDock.Core.Localization.Text("左下の「保存エンジンを準備」を押してください。"));
            if (request.UseLogin && sessionProvider is not null) session = await sessionProvider(request, ct);
            ct.ThrowIfCancellationRequested();
            dispatch(() => item.Detail = session is null ? ClipDock.Core.Localization.Text("動画情報を解析しています…") : ClipDock.Core.Localization.Text("ログイン情報を使って動画情報を解析しています…"));
            DownloadPlan.PrepareWorkDirectory(item.OutputDirectory, item.Id);
            using var process = new Process { StartInfo = DownloadPlan.Create(request, tools, item.OutputDirectory, item.Id, session?.FilePath) };
            process.Start();
            // Nothing is ever typed into the engine: an immediate EOF makes any prompt fail fast instead of hanging the queue.
            try { process.StandardInput.Close(); } catch (IOException) { }
            using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
            bool HandleLine(string line)
            {
                    var progress = DownloadProgress.Parse(line);
                    if (progress is not null)
                    {
                        progress = progressTracker.Apply(progress);
                        dispatch(() =>
                        {
                            item.Percent = progress.Percent;
                            item.State = progress.Finished ? "converting" : "downloading";
                            item.Detail = progress.Finished ? ClipDock.Core.Localization.Text("結合・変換を処理中です。完了までお待ちください…") :
                                $"{progress.Percent:0.0}%  ·  {progress.BytesPerSecond / 1024 / 1024:0.0} MB/s" +
                                (progress.SecondsRemaining is > 0 ? ClipDock.Core.Localization.Format($"  ·  残り約 {TimeSpan.FromSeconds(Math.Min(progress.SecondsRemaining.Value, 8640000)):hh\\:mm\\:ss}") : "");
                        });
                    }
                    else if (line.StartsWith("CLIPDOCK_PARTS ", StringComparison.Ordinal)) progressTracker.SetParts(line[15..]);
                    else if (line.StartsWith("CLIPDOCK_TITLE ", StringComparison.Ordinal))
                    {
                        try { var title = JsonSerializer.Deserialize<string>(line[15..]); if (!string.IsNullOrWhiteSpace(title)) dispatch(() => item.Title = DownloadRequest.CleanText(title, 250)); } catch (JsonException) { }
                    }
                    else if (line.StartsWith("CLIPDOCK_FILE ", StringComparison.Ordinal))
                    {
                        try { resultPath = JsonSerializer.Deserialize<string>(line[14..]); } catch (JsonException) { }
                    }
                    else return false;
                    return true;
            }
            async Task ReadOutput()
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line) HandleLine(line);
            }
            async Task ReadErrors()
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    // yt-dlp may route progress to stderr when --print is in use.
                    if (HandleLine(line) || string.IsNullOrWhiteSpace(line)) continue;
                    // Do not retain signed URL query strings in the diagnostic history.
                    var safe = UrlPattern().Replace(session?.Redact(line) ?? line, match => Uri.TryCreate(match.Value, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "[URL]");
                    errors.Enqueue(DownloadRequest.CleanText(safe, 500));
                    while (errors.Count > 6) errors.Dequeue();
                }
            }
            var stdout = ReadOutput();
            var stderr = ReadErrors();
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException(errors.Count > 0 ? string.Join("\n", errors) : ClipDock.Core.Localization.Text("動画を取得できませんでした。ページを再読み込みして、もう一度送信してください。"));
            if (resultPath is null || !DownloadPlan.IsOutputFile(resultPath, item.OutputDirectory, request.Format) || !File.Exists(resultPath))
                throw new IOException(ClipDock.Core.Localization.Text("このURLから保存できる動画が見つかりませんでした。動画そのもののページのURLを入力してください。"));
            finish = () => { item.FilePath = resultPath; item.Percent = 100; item.State = "completed"; item.Detail = Path.GetFileName(resultPath); };
            try { DownloadPlan.CleanWorkDirectory(item.OutputDirectory, item.Id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (OperationCanceledException) { finish = () => { item.State = "cancelled"; item.Detail = ClipDock.Core.Localization.Text("中断しました。再試行すると取得済みの断片から再開を試みます。"); }; }
        catch (Exception ex) { var message = session?.Redact(ex.Message) ?? ex.Message; finish = () => { item.State = "failed"; item.Detail = DownloadFailure.Describe(item.Request.Url, message); }; }
        finally
        {
            var cleanupFailed = false;
            try { session?.Dispose(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupFailed = true; }
            dispatch(() => { finish?.Invoke(); if (cleanupFailed) item.Detail += ClipDock.Core.Localization.Text("\n一時ログイン情報を削除できませんでした。アプリを再起動して削除を再試行してください。"); });
        }
    }
}
