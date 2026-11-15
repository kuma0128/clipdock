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
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
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
