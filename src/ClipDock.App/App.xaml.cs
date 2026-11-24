using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using ClipDock.Core;
using Microsoft.Win32;

namespace ClipDock.App;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activation;
    private bool? darkPalette;
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
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplySystemPalette();
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ClipDock", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow(activation);
        if (!background) MainWindow.Show();
    }
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.InvokeAsync(ApplySystemPalette);

    private void ApplySystemPalette()
    {
        const string personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        var light = Registry.GetValue(personalize, "AppsUseLightTheme", null)
            ?? Registry.GetValue(personalize, "SystemUsesLightTheme", 1);
        var dark = light is 0;
        if (darkPalette == dark) return;
        foreach (var (key, lightColor, darkColor) in DesktopPalette.Brushes)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : lightColor));
            brush.Freeze();
            Resources[key] = brush;
        }
        darkPalette = dark;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        activation?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(int processId);
}
