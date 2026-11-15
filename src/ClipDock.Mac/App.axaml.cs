using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace ClipDock.Mac;

public partial class App : Application
{
    // Set by --smoke: the windows exercise themselves and report here instead of waiting for a person.
    internal static string? SmokeResultFile { get; set; }
    internal static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // macOS already keeps one instance per app bundle, so no cross-process activation is needed.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            desktop.MainWindow = window;
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                _ = ConfirmDialog.ShowAsync(window, "ClipDock", args.Exception.Message, "閉じる");
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void About_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            _ = ConfirmDialog.ShowAsync(window, "ClipDock について", $"ClipDock {Version}\n動画をMP4、音声をMP3として保存します。", "閉じる");
    }
}
