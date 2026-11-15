using System.ComponentModel;
using System.Diagnostics;

namespace ClipDock.Mac;

// Opens files, folders and pages with the system's default application.
internal static class Shell
{
    internal static void Open(string target)
    {
        if (OperatingSystem.IsMacOS()) Start("/usr/bin/open", target);
        else Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    // Shows the file selected in Finder (Explorer on Windows).
    internal static void Reveal(string file)
    {
        if (OperatingSystem.IsMacOS()) Start("/usr/bin/open", "-R", file);
        else Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            $"/select,\"{Path.GetFullPath(file)}\"") { UseShellExecute = false })?.Dispose();
    }

    private static void Start(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    internal static bool IsOpenFailure(Exception ex) => ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException;
}
