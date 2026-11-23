using Avalonia;
using ClipDock.Core;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace ClipDock.Mac;

public partial class App : Application
{
    // Set by --smoke: the windows exercise themselves and report here instead of waiting for a person.
    internal static string? SmokeResultFile { get; set; }
    // Set by --background (the login item): the window stays closed and ClipDock waits in the menu bar.
    internal static bool StartHidden { get; set; }
    // Kept open for the life of the process; see Program.InstanceLock.
    internal static FileStream? InstanceLock { get; set; }
    internal static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
    private MainWindow? window;

    public override void Initialize()
    {
        // Until a language is chosen in the app, the preferred languages in macOS's Language & Region decide.
        Localization.Initialize(Path.Combine(AppPaths.Data, "settings.json"), MacApp.PreferredLanguages());
        AvaloniaXamlLoader.Load(this);
        foreach (var resource in Localization.Resources) Resources[resource.Key] = resource.Value;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window leaves ClipDock in the menu bar. It ends when MainWindow calls Quit, or when macOS
            // asks (⌘Q, Quit in the Dock, log out), which MainWindow answers in OnShutdownRequested.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            window = new MainWindow();
            desktop.ShutdownRequested += window.OnShutdownRequested;
            // The lifetime shows its MainWindow at start, so after a login it is only assigned once running.
            if (StartHidden) Dispatcher.UIThread.Post(() => desktop.MainWindow = window);
            else desktop.MainWindow = window;
            // Opening ClipDock from Finder, Launchpad or Spotlight while it runs in the menu bar brings the window back.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activation)
                activation.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) window.ShowFromMenuBar(); };
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                if (window.IsVisible) _ = ConfirmDialog.ShowAsync(window, "ClipDock", args.Exception.Message, ClipDock.Core.Localization.Text("閉じる"));
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Ends the process once the window has finished closing; never from inside a shutdown macOS already started.
    internal static void Quit() => Dispatcher.UIThread.Post(() => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());

    private void About_Click(object? sender, EventArgs e)
    {
        if (window is { IsVisible: true })
            _ = ConfirmDialog.ShowAsync(window, ClipDock.Core.Localization.Text("ClipDock について"), ClipDock.Core.Localization.Format($"ClipDock {Version}\n動画をMP4、音声をMP3として保存します。"), ClipDock.Core.Localization.Text("閉じる"));
    }
}
