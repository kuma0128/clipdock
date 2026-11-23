using System.Runtime.InteropServices;

namespace ClipDock.Mac;

// The few AppKit calls Avalonia does not expose, sent through the Objective-C runtime that every Mac has;
// no native code is shipped. Elsewhere (the Avalonia build on Windows during development) they do nothing.
internal static class MacApp
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    // The Dock icon and the app menu while the window is open; only the menu bar icon while it is closed.
    internal static void ShowInDock(bool show)
    {
        if (OperatingSystem.IsMacOS()) Send(Application, Selector("setActivationPolicy:"), show ? 0 : 1); // Regular : Accessory
    }

    // Brings ClipDock in front of other apps, as when it is chosen from the menu bar icon.
    internal static void Activate()
    {
        if (OperatingSystem.IsMacOS()) Send(Application, Selector("activateIgnoringOtherApps:"), 1);
    }

    // With the login item in place, macOS must not also reopen ClipDock (and its window) for "Reopen windows when
    // logging back in": the login item already starts it in the menu bar.
    internal static void RelaunchOnLogin(bool relaunch)
    {
        if (OperatingSystem.IsMacOS()) Send(Application, Selector(relaunch ? "enableRelaunchOnLogin" : "disableRelaunchOnLogin"));
    }

    private static nint Application => Send(objc_getClass("NSApplication"), Selector("sharedApplication"));
    private static nint Selector(string name) => sel_registerName(name);

    [DllImport(Runtime)] private static extern nint objc_getClass(string name);
    [DllImport(Runtime)] private static extern nint sel_registerName(string name);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint argument);
}
