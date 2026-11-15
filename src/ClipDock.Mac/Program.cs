using Avalonia;
using ClipDock.Core;

namespace ClipDock.Mac;

internal static class Program
{
    // --data-dir <path>      keep settings, history and the engine in another folder (development and checks)
    // --engine-check <path>  install the engine there and save synthetic media with it, without opening a window
    // --smoke <file>         open the windows, exercise the login browser, write the result to <file>
    [STAThread]
    public static int Main(string[] args)
    {
        string? Value(string name) => Array.IndexOf(args, name) is >= 0 and var index && index + 1 < args.Length ? args[index + 1] : null;
        if (Value("--data-dir") is { } data) AppPaths.UseDataDirectory(Path.GetFullPath(data));
        if (Value("--engine-check") is { } directory) return EngineCheck.RunAsync(Path.GetFullPath(directory)).GetAwaiter().GetResult();
        if (Value("--smoke") is { } result) App.SmokeResultFile = Path.GetFullPath(result);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
