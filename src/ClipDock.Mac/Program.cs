using Avalonia;
using ClipDock.Core;

namespace ClipDock.Mac;

internal static class Program
{
    // --data-dir <path>      keep settings, history and the engine in another folder (development and checks)
    // --background           started at login: stay in the menu bar until the window is opened from there
    // --engine-check <path>  install the engine there and save synthetic media with it, without opening a window
    // --smoke <file>         open the windows, exercise the login browser, write the result to <file>
    // --login-item-check <folder>  write the login item for this copy into <folder> instead of ~/Library/LaunchAgents
    [STAThread]
    public static int Main(string[] args)
    {
        string? Value(string name) => Array.IndexOf(args, name) is >= 0 and var index && index + 1 < args.Length ? args[index + 1] : null;
        if (Value("--data-dir") is { } data) AppPaths.UseDataDirectory(Path.GetFullPath(data));
        if (Value("--engine-check") is { } directory) return EngineCheck.RunAsync(Path.GetFullPath(directory)).GetAwaiter().GetResult();
        if (Value("--login-item-check") is { } agents) return CheckLoginItem(Path.GetFullPath(agents));
        if (Value("--smoke") is { } result) App.SmokeResultFile = Path.GetFullPath(result);
        App.StartHidden = args.Contains(LoginItem.BackgroundArgument);
        HistoryFile.PrepareDataDirectory();
        App.InstanceLock = InstanceLock();
        // Opened at login while ClipDock already runs (opened by hand, or by another login item): that copy carries on.
        if (App.StartHidden && App.InstanceLock is null) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // At login the Dock stays empty; the icon appears once the window is opened from the menu bar.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = !App.StartHidden });

    // Held while ClipDock runs, so a second copy started at login can tell. The file itself stays empty.
    private static FileStream? InstanceLock()
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(Path.Combine(AppPaths.Data, "instance.lock"), options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Used by scripts/test-mac.sh: proves the bundle finds its own executable and writes a valid agent, away from the real folder.
    private static int CheckLoginItem(string agents)
    {
        if (LoginItem.BundleExecutable(Environment.ProcessPath) is not { } executable)
        {
            Console.Error.WriteLine("FAIL not running from an app bundle: " + Environment.ProcessPath);
            return 1;
        }
        var item = new LoginItem(agents);
        item.Enable(executable);
        Console.WriteLine(item.AgentFile);
        return item.Targets(executable) ? 0 : 1;
    }
}
