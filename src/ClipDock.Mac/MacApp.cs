using System.Runtime.InteropServices;

namespace ClipDock.Mac;

// The few AppKit and Foundation calls Avalonia does not expose, sent through the Objective-C runtime that every Mac has;
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

    // The preferred languages in System Settings → General → Language & Region, most preferred first ("ja-JP",
    // "zh-Hans-CN"). An app opened from Finder gets no LANG, from which .NET would take CurrentUICulture, so the list
    // comes from macOS itself.
    internal static IReadOnlyList<string> PreferredLanguages()
    {
        if (!OperatingSystem.IsMacOS()) return [System.Globalization.CultureInfo.CurrentUICulture.Name];
        var pool = objc_autoreleasePoolPush();
        try
        {
            var languages = Send(objc_getClass("NSLocale"), Selector("preferredLanguages"));
            var result = new List<string>();
            for (nint index = 0, count = Send(languages, Selector("count")); index < count; index++)
                if (Marshal.PtrToStringUTF8(Send(Send(languages, Selector("objectAtIndex:"), index), Selector("UTF8String"))) is { } language)
                    result.Add(language);
            return result;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static nint Application => Send(objc_getClass("NSApplication"), Selector("sharedApplication"));
    private static nint Selector(string name) => sel_registerName(name);

    [DllImport(Runtime)] private static extern nint objc_getClass(string name);
    [DllImport(Runtime)] private static extern nint sel_registerName(string name);
    [DllImport(Runtime)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(Runtime)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint argument);
}
