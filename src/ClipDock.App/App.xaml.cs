using System.IO;
using System.Text.Json;
using System.Windows;
using ClipDock.Core;

namespace ClipDock.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        instance = new Mutex(true, @"Local\ClipDock-" + user, out var first);
        if (!first)
        {
            // Also bring the existing window forward when the desktop shortcut is clicked.
            // Best effort: this runs before the error dialog is wired up, and failing to raise the
            // other window must not end in a crash report for a second launch.
            try
            {
                Directory.CreateDirectory(AppPaths.Inbox);
                var request = new DownloadRequest { Action = "open" };
                var file = Path.Combine(AppPaths.Inbox, Guid.NewGuid().ToString("N") + ".request");
                File.WriteAllBytes(file + ".tmp", WindowsProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(request, Json.Options)));
                File.Move(file + ".tmp", file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ClipDock", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
