using System.Windows;
using ClipDock.Core;

namespace ClipDock.App;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activation;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Match the Mac development option without touching the user's installed profile.
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length)
            AppPaths.UseDataDirectory(System.IO.Path.GetFullPath(e.Args[dataIndex + 1]));
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        if (dataIndex >= 0)
            user += "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Data)))[..16];
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClipDock-activate-" + user);
        instance = new Mutex(true, @"Local\ClipDock-" + user, out var first);
        if (!first)
        {
            activation.Set();
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ClipDock", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow(activation);
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { activation?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
