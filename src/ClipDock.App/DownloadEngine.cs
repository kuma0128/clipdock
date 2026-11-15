using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipDock.Core;

namespace ClipDock.App;

public static partial class DownloadEngine
{
    [GeneratedRegex(@"https?://[^\s""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    public static async Task RunAsync(DownloadItem item, string tools, Action<Action> dispatch, CancellationToken ct)
    {
        string? cookieFile = null;
        var errors = new Queue<string>();
        string? resultPath = null;
        Action? finish = null;
        try
        {
            // Claim the item before the first async file operation. Cancellation during
            // cookie preparation must stop this operation, not just change its UI label.
            dispatch(() => { item.State = "downloading"; item.Detail = "動画情報を解析しています…"; });
            ct.ThrowIfCancellationRequested();
            var request = item.Request;
            Directory.CreateDirectory(item.OutputDirectory);
            if (!ToolInstaller.IsReady(tools)) throw new IOException("左下の「エンジンを準備 / 更新」を押してください。");
            if (request.Cookies.Count > 0)
            {
                Directory.CreateDirectory(AppPaths.Secrets);
                cookieFile = Path.Combine(AppPaths.Secrets, item.Id + ".cookies");
                await File.WriteAllTextAsync(cookieFile, DownloadPlan.CookieFile(request), new UTF8Encoding(false), ct);
            }
            ct.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = DownloadPlan.Create(request, tools, item.OutputDirectory, item.Id, cookieFile) };
            process.Start();
            // Nothing is ever typed into the engine: an immediate EOF makes any prompt fail fast instead of hanging the queue.
            try { process.StandardInput.Close(); } catch (IOException) { }
            using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
            bool HandleLine(string line)
            {
                    var progress = DownloadProgress.Parse(line);
                    if (progress is not null)
                    {
                        dispatch(() =>
                        {
                            item.Percent = progress.Percent;
                            item.State = progress.Finished ? "converting" : "downloading";
                            item.Detail = progress.Finished ? "結合・変換を処理中です。完了までお待ちください…" :
                                $"{progress.Percent:0.0}%  ·  {progress.BytesPerSecond / 1024 / 1024:0.0} MB/s" +
                                (progress.SecondsRemaining is > 0 ? $"  ·  残り約 {TimeSpan.FromSeconds(Math.Min(progress.SecondsRemaining.Value, 8640000)):hh\\:mm\\:ss}" : "");
                        });
                    }
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
                    // Do not retain signed URL query strings or cookies in the diagnostic history.
                    var safe = UrlPattern().Replace(line, match => Uri.TryCreate(match.Value, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "[URL]");
                    foreach (var cookie in request.Cookies.Where(c => c.Value.Length > 3)) safe = safe.Replace(cookie.Value, "[Cookie]", StringComparison.Ordinal);
                    errors.Enqueue(DownloadRequest.CleanText(safe, 500));
                    while (errors.Count > 6) errors.Dequeue();
                }
            }
            var stdout = ReadOutput();
            var stderr = ReadErrors();
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException(errors.Count > 0 ? string.Join("\n", errors) : "動画を取得できませんでした。ページを再読み込みして、もう一度送信してください。");
            if (resultPath is null || !DownloadPlan.IsOutputFile(resultPath, item.OutputDirectory) || !File.Exists(resultPath))
                throw new IOException("MP4ファイルの保存を確認できませんでした。保存先を確認してください。");
            finish = () => { item.FilePath = resultPath; item.Percent = 100; item.State = "completed"; item.Detail = Path.GetFileName(resultPath); };
        }
        catch (OperationCanceledException) { finish = () => { item.State = "cancelled"; item.Detail = "中断しました。再試行すると取得済みの断片から再開を試みます。"; }; }
        catch (Exception ex) { finish = () => { item.State = "failed"; item.Detail = ex.Message; }; }
        finally
        {
            var cleaned = cookieFile is null || await DeleteSecretAsync(cookieFile);
            // Terminal states enable retry/clear/close. Publish them only after the cookie
            // cleanup has finished, so those actions cannot abandon an active cleanup task.
            dispatch(() =>
            {
                item.Request = item.Request with { Cookies = [] };
                finish?.Invoke();
                if (!cleaned)
                {
                    item.State = "failed";
                    item.Detail = "一時ログイン情報を削除できませんでした。ClipDockを再起動して削除を再試行してください。";
                }
            });
        }
    }

    // A killed engine or a virus scanner can hold the jar for a moment; plaintext cookies must not be left behind by one failed attempt.
    private static async Task<bool> DeleteSecretAsync(string file)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { File.Delete(file); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(200); }
        }
        return false;
    }
}
