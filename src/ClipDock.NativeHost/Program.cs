using System.Diagnostics;
using System.Text.Json;
using ClipDock.Core;

if (args is ["--install-tools", var destination])
{
    try { await ToolInstaller.InstallAsync(Path.GetFullPath(destination), new Progress<string>(Console.Error.WriteLine)); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}

if (args.Length == 0 || args[0] != Integration.Origin) return 1;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var output = Console.OpenStandardOutput();
string? queuedFile = null;
try
{
    var request = (await NativeProtocol.ReadAsync<DownloadRequest>(Console.OpenStandardInput(), timeout.Token)).Validated();
    if (request.Action == "ping")
    {
        await NativeProtocol.WriteAsync(output, new { ok = true, version = "1.0.0", ready = ToolInstaller.IsReady(ToolInstaller.FindTools()) }, timeout.Token);
        return 0;
    }
    var executable = Path.Combine(AppContext.BaseDirectory, "ClipDock.exe");
    if (!File.Exists(executable)) throw new FileNotFoundException("ClipDock.exeが見つかりません。アプリを再配置してください。");
    Directory.CreateDirectory(AppPaths.Inbox);
    if (Directory.EnumerateFiles(AppPaths.Inbox, "*.request").Take(101).Count() >= 100)
        throw new IOException("待機中のリクエストが多すぎます。ClipDockを開いてください。");
    var id = Guid.NewGuid().ToString("N");
    queuedFile = Path.Combine(AppPaths.Inbox, id + ".request");
    var temporary = queuedFile + ".tmp";
    await File.WriteAllBytesAsync(temporary, WindowsProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(request, Json.Options)), timeout.Token);
    File.Move(temporary, queuedFile);
    using var app = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true });
    await NativeProtocol.WriteAsync(output, new { ok = true, id, message = "ClipDockに追加しました" }, timeout.Token);
    return 0;
}
catch (Exception ex)
{
    if (queuedFile is not null)
    {
        try { File.Delete(queuedFile); File.Delete(queuedFile + ".tmp"); }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
    }
    // InvalidDataException (size limit, empty message) is not an IOException but carries a message written for the user.
    var explained = ex is ArgumentException or IOException or InvalidDataException;
    try { await NativeProtocol.WriteAsync(output, new { ok = false, error = explained ? ex.Message : "アプリとの通信に失敗しました。ClipDockのChrome連携設定を確認してください。" }); }
    catch (Exception write) when (write is IOException or ObjectDisposedException) { }
    return 1;
}
