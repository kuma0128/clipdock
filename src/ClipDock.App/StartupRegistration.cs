using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClipDock.App;

// Starts ClipDock in the notification area when the user signs in. Only the current user's Run entry is used.
// Windows' own switch (Settings > Apps > Startup, Task Manager) is stored under StartupApproved and is respected:
// ClipDock clears it only when the user turns the option on again inside the app.
[SupportedOSPlatform("windows")]
internal sealed class StartupRegistration(string runKey = StartupRegistration.RunKey, string approvalKey = StartupRegistration.ApprovalKey)
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string ValueName = "ClipDock";
    internal const string BackgroundArgument = "--background";

    internal static string Command(string executable) => $"\"{Path.GetFullPath(executable)}\" {BackgroundArgument}";

    // The executable a Run command starts, or null when the command is not in the quoted form ClipDock writes.
    internal static string? Target(string? command)
    {
        if (command is not { Length: > 2 } || command[0] != '"') return null;
        var end = command.IndexOf('"', 1);
        if (end <= 1) return null;
        try { return Path.GetFullPath(command[1..end]); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal bool Targets(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey);
        return Target(key?.GetValue(ValueName) as string) is { } target &&
            string.Equals(target, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
    }

    // Windows marks a disabled startup entry with an odd first byte (0x03); enabled entries use 0x02 or have no value.
    internal bool DisabledByWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(approvalKey);
        return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
    }

    internal bool IsEnabled(string executable) => Targets(executable) && !DisabledByWindows();

    // At launch (clearWindowsSwitch: false) this only keeps the entry pointing at this copy, for example after a move.
    internal void Enable(string executable, bool clearWindowsSwitch)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(runKey))
        {
            var command = Command(executable);
            if (key.GetValue(ValueName) as string != command) key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        if (!clearWindowsSwitch) return;
        using var approval = Registry.CurrentUser.OpenSubKey(approvalKey, true);
        if (approval?.GetValue(ValueName) is not null) approval.DeleteValue(ValueName, false);
    }

    internal void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
        key?.DeleteValue(ValueName, false);
    }
}
