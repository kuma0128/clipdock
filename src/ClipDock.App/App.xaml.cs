using System.Runtime.InteropServices;
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
        // Started by Windows at sign-in: stay in the notification area until the user opens the window.
        var background = e.Args.Contains(StartupRegistration.BackgroundArgument);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        if (dataIndex >= 0)
            user += "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Data)))[..16];
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClipDock-activate-" + user);
        instance = new Mutex(true, @"Local\ClipDock-" + user, out var first);
        if (!first)
        {
            if (!background)
            {
                // This launch came from the user; let the running copy bring its (possibly hidden) window to the front.
                AllowSetForegroundWindow(-1);
                activation.Set();
            }
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ClipDock", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow(activation);
        if (!background) MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { activation?.Dispose(); instance?.Dispose(); base.OnExit(e); }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(int processId);
}
