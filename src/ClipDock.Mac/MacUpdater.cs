using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipDock.Core;

namespace ClipDock.Mac;

// Self-update on macOS: after ClipDock quits, a small shell script swaps this app bundle for the one inside the
// downloaded disk image (already verified against the official site's SHA-256) and opens ClipDock again.
// Nothing of the running bundle is in use while it is replaced, and the old bundle is restored if a step fails.
internal static class MacUpdater
{
    // The bundle (…/ClipDock.app) this process may replace: a lasting install in a folder the user can write to.
    // The disk image, a translocated copy, a development build and --data-dir runs never update themselves.
    internal static string? UpdatableBundle(string? processPath)
    {
        if (AppPaths.UsesCustomDataDirectory || !OperatingSystem.IsMacOS()) return null;
        if (LoginItem.BundleExecutable(processPath) is not { } executable || LoginItem.IsTemporary(executable)) return null;
        var bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executable)))!;
        var parent = Path.GetDirectoryName(bundle);
        return parent is not null && access(parent, 2) == 0 && access(bundle, 2) == 0 ? bundle : null; // W_OK
    }

    // Starts the script in its own process group: launchd ends every process left in ClipDock's group when it quits.
    internal static void Start(string bundle, string image, Version version, bool openWindow, string log)
    {
        var script = Path.Combine(Path.GetTempPath(), $"clipdock-update-{Guid.NewGuid():N}.sh");
        File.WriteAllText(script, Script);
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", "set -m; /bin/sh \"$0\" \"$@\" &", script, Environment.ProcessId.ToString(), image, bundle,
            version.ToString(3), openWindow ? "window" : "background", log })
            start.ArgumentList.Add(argument);
        using var launcher = Process.Start(start) ?? throw new IOException("The update helper did not start.");
        launcher.WaitForExit(5000);
    }

    // Arguments: ClipDock's process id, disk image, bundle to replace, expected version, window|background (none in
    // the checks), log file.
    internal const string Script = """
        #!/bin/sh
        pid="$1"; image="$2"; bundle="$3"; version="$4"; mode="$5"; log="$6"
        exec >>"$log" 2>&1
        echo "$(date): updating $bundle to $version"
        reopen() {
          if [ "$mode" = window ]; then /usr/bin/open "$bundle"
          elif [ "$mode" = background ]; then /usr/bin/open "$bundle" --args --background; fi
        }
        waited=0
        while kill -0 "$pid" 2>/dev/null; do
          waited=$((waited + 1))
          if [ "$waited" -gt 240 ]; then echo "ClipDock did not quit"; exit 1; fi
          sleep 0.25
        done
        mount=$(/usr/bin/mktemp -d /tmp/clipdock-update.XXXXXX) || { reopen; exit 1; }
        if ! /usr/bin/hdiutil attach -nobrowse -readonly -noautoopen -mountpoint "$mount" "$image" >/dev/null; then
          rmdir "$mount"; echo "could not open the disk image"; reopen; exit 1
        fi
        new="$mount/ClipDock.app"
        found=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$new/Contents/Info.plist" 2>/dev/null)
        id=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$new/Contents/Info.plist" 2>/dev/null)
        status=1
        if [ "$found" = "$version" ] && [ "$id" = "app.clipdock.ClipDock" ]; then
          staged="$bundle.update"; previous="$bundle.previous"
          rm -rf "$staged" "$previous"
          if /usr/bin/ditto "$new" "$staged" && mv "$bundle" "$previous"; then
            if mv "$staged" "$bundle"; then rm -rf "$previous"; status=0; else mv "$previous" "$bundle"; fi
          fi
          rm -rf "$staged"
        else
          echo "the disk image holds $id $found"
        fi
        /usr/bin/hdiutil detach "$mount" -quiet; rmdir "$mount" 2>/dev/null
        rm -f "$image" "$0"
        echo "$(date): finished with status $status"
        reopen
        exit $status
        """;

    [DllImport("libc", SetLastError = true)] private static extern int access(string path, int mode);
}
