using ClipDock.Core;

namespace ClipDock.Mac;

// macOS has no DPAPI. History and settings are files only the current user can read (mode 600 inside a 700 folder).
internal static class HistoryFile
{
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerFolder = OwnerFile | UnixFileMode.UserExecute;

    internal static void PrepareDataDirectory()
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(AppPaths.Data); return; }
        if (Directory.Exists(AppPaths.Data)) File.SetUnixFileMode(AppPaths.Data, OwnerFolder);
        else Directory.CreateDirectory(AppPaths.Data, OwnerFolder);
    }

    internal static byte[] Read(string file)
    {
        var bytes = File.ReadAllBytes(file);
        return OperatingSystem.IsWindows() ? WindowsProtection.Unprotect(bytes) : bytes;
    }

    // Replaces the file in one step so a crash never leaves half a history behind.
    // plain: settings stay readable JSON on every platform; history is DPAPI-protected where Windows offers it.
    internal static void Write(string file, byte[] contents, bool plain = false)
    {
        var temporary = file + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsWindows()) { if (!plain) contents = WindowsProtection.Protect(contents); }
        else options.UnixCreateMode = OwnerFile;
        using (var stream = new FileStream(temporary, options)) stream.Write(contents);
        File.Move(temporary, file, true);
    }
}
