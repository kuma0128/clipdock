using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClipDock.App;

// The notification-area icon. Shell_NotifyIcon is used directly so the package does not need Windows Forms.
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x43; // WM_APP + n
    private const int WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_USER = 0x04, NIIF_RESPECT_QUIET_TIME = 0x80;
    private const uint OpenCommand = 1, ExitCommand = 2;
    private static readonly int TaskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");
    private readonly HwndSource window;
    private readonly DispatcherTimer retry = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Action open, exit;
    private readonly nint icon;
    private readonly bool ownsIcon;
    private string text = "ClipDock";
    private int attempts;
    private bool disposed;

    internal TrayIcon(Action open, Action exit)
    {
        this.open = open;
        this.exit = exit;
        // A hidden top-level window: it receives TaskbarCreated after Explorer restarts and can own the menu.
        window = new HwndSource(new HwndSourceParameters("ClipDock notification icon")
        {
            Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x00000080 // WS_POPUP, WS_EX_TOOLWINDOW
        });
        window.AddHook(WindowMessage);
        (icon, ownsIcon) = LoadIcon();
        // At sign-in the taskbar may not accept icons yet; keep trying for a minute, then wait for TaskbarCreated.
        retry.Tick += (_, _) => { if (Add() || ++attempts >= 30) retry.Stop(); };
        if (!Add()) retry.Start();
    }

    internal bool IsVisible { get; private set; }

    internal string Text
    {
        get => text;
        set
        {
            // The notification area shows at most 127 characters.
            var next = value.Length > 127 ? value[..127] : value;
            if (next == text) return;
            text = next;
            if (IsVisible) Update(NIM_MODIFY, NIF_TIP | NIF_SHOWTIP);
        }
    }

    internal void Notify(string title, string message)
    {
        if (!IsVisible) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = message.Length > 255 ? message[..255] : message;
        data.dwInfoFlags = NIIF_USER | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private bool Add()
    {
        if (disposed) return false;
        if (IsVisible) return true;
        if (!Update(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP)) return false;
        var data = Data(0);
        data.uVersion = 4; // NOTIFYICON_VERSION_4: NIN_SELECT for clicks, WM_CONTEXTMENU for the menu
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        IsVisible = true;
        return true;
    }

    private bool Update(uint message, uint flags)
    {
        var data = Data(flags);
        return Shell_NotifyIcon(message, ref data);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = window.Handle, uID = 1, uFlags = flags,
        uCallbackMessage = CallbackMessage, hIcon = icon, szTip = text, szInfo = "", szInfoTitle = ""
    };

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            handled = true;
            switch ((int)(lParam & 0xFFFF))
            {
                case NIN_SELECT or NIN_KEYSELECT or NIN_BALLOONUSERCLICK:
                    window.Dispatcher.BeginInvoke(open);
                    break;
                case WM_CONTEXTMENU:
                    ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;
            }
        }
        else if (message == TaskbarCreated)
        {
            // Explorer restarted and forgot every icon.
            IsVisible = false;
            if (!Add()) { attempts = 0; retry.Start(); }
        }
        return 0;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        uint command;
        try
        {
            AppendMenu(menu, 0, OpenCommand, ClipDock.Core.Localization.Text("ClipDockを開く"));
            AppendMenu(menu, 0x800, 0, null); // MF_SEPARATOR
            AppendMenu(menu, 0, ExitCommand, ClipDock.Core.Localization.Text("ClipDockを終了"));
            SetMenuDefaultItem(menu, OpenCommand, 0);
            // Without the foreground the menu would stay open after clicking elsewhere.
            SetForegroundWindow(window.Handle);
            command = TrackPopupMenuEx(menu, 0x0100 | 0x0002 | 0x0080, x, y, window.Handle, 0); // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY
            PostMessage(window.Handle, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
        if (command == OpenCommand) window.Dispatcher.BeginInvoke(open);
        else if (command == ExitCommand) window.Dispatcher.BeginInvoke(exit);
    }

    // The application icon embedded in ClipDock.exe, at the notification area's size for the current display.
    private static (nint Handle, bool Owned) LoadIcon()
    {
        var size = (uint)Math.Max(16, GetSystemMetrics(49)); // SM_CXSMICON
        if (Environment.ProcessPath is { } path && SHDefExtractIcon(path, 0, 0, out var large, out var small, size << 16 | size) == 0)
        {
            if (large != 0) DestroyIcon(large);
            if (small != 0) return (small, true);
        }
        return (LoadStockIcon(0, 32512), false); // The shared IDI_APPLICATION icon is never destroyed.
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        retry.Stop();
        if (IsVisible) Update(NIM_DELETE, 0);
        IsVisible = false;
        window.RemoveHook(WindowMessage);
        window.Dispose();
        if (ownsIcon) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out nint large, out nint small, uint size);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadStockIcon(nint instance, nint name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
