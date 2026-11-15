using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipDock.App;

internal sealed class BrowserOverlay : IDisposable
{
    private readonly Action<BrowserAddressResult> receive;
    private readonly BrowserAddressReader reader = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Window pill;
    private CancellationTokenSource? readCancellation;
    private nint browserWindow;
    private nint handle;
    private bool enabled, disposed, busy;
    private DateTime lastHover;

    internal BrowserOverlay(Action<BrowserAddressResult> receive)
    {
        this.receive = receive;
        var button = new Button
        {
            Content = ClipDock.Core.Localization.Text("↓  ClipDockに取り込む"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x13, 0x2B, 0x1D)),
            Background = new SolidColorBrush(Color.FromRgb(0xA5, 0xE7, 0xBA)),
            BorderThickness = new Thickness(0), Padding = new Thickness(12, 7, 12, 7), Focusable = false,
            ToolTip = ClipDock.Core.Localization.Text("このページのURLをClipDockで確認する")
        };
        AutomationProperties.SetName(button, ClipDock.Core.Localization.Text("ClipDockに取り込む"));
        button.Click += Capture_Click;
        pill = new Window
        {
            Title = ClipDock.Core.Localization.Text("ClipDock 保存タブ"), Width = 220, Height = 38, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            Focusable = false,
            Content = new Border { Background = button.Background, CornerRadius = new CornerRadius(7), Child = button }
        };
        pill.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(pill).Handle;
            BrowserWindow.SetWindowLongPtr(handle, -20, BrowserWindow.GetWindowLongPtr(handle, -20) | 0x08000080);
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        };
        timer.Tick += (_, _) => UpdatePosition();
    }

    internal bool Enabled
    {
        get => enabled;
        set
        {
            if (disposed || enabled == value) return;
            enabled = value;
            if (enabled) timer.Start();
            else { timer.Stop(); readCancellation?.Cancel(); pill.Hide(); browserWindow = 0; }
        }
    }

    private static nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; }
        return 0;
    }

    private void UpdatePosition()
    {
        if (!enabled || disposed || busy) return;
        var foreground = BrowserWindow.GetForegroundWindow();
        if (!BrowserWindow.TryGet(foreground, out var browser) || !BrowserWindow.GetCursorPos(out var cursor))
        { pill.Hide(); browserWindow = 0; return; }
        if (browserWindow != foreground) { pill.Hide(); browserWindow = foreground; lastHover = DateTime.MinValue; }
        var width = (int)Math.Round(220 * browser.Scale);
        var height = (int)Math.Round(38 * browser.Scale);
        var left = Math.Clamp(browser.Left + (browser.Right - browser.Left - width) / 2, browser.WorkArea.Left, Math.Max(browser.WorkArea.Left, browser.WorkArea.Right - width));
        var top = Math.Clamp(browser.Top + (int)Math.Round(2 * browser.Scale), browser.WorkArea.Top, Math.Max(browser.WorkArea.Top, browser.WorkArea.Bottom - height));
        var hovered = cursor.X >= left && cursor.X <= left + width && cursor.Y >= top - 3 * browser.Scale && cursor.Y <= top + (pill.IsVisible ? height + 8 * browser.Scale : 16 * browser.Scale);
        if (hovered) lastHover = DateTime.UtcNow;
        if (!hovered && DateTime.UtcNow - lastHover > TimeSpan.FromMilliseconds(450)) { pill.Hide(); return; }
        if (!pill.IsVisible)
        {
            new WindowInteropHelper(pill).EnsureHandle();
            BrowserWindow.SetWindowPos(handle, -1, left, top, width, height, 0x0010);
            pill.Show();
        }
        BrowserWindow.SetWindowPos(handle, -1, left, top, width, height, 0x0010);
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (!enabled || disposed || busy || browserWindow == 0 || BrowserWindow.GetForegroundWindow() != browserWindow) return;
        busy = true;
        pill.Hide();
        using var cancellation = readCancellation = new CancellationTokenSource();
        try
        {
            var result = await reader.ReadAsync(browserWindow, cancellation.Token);
            if (enabled && !disposed && !cancellation.IsCancellationRequested) receive(result);
        }
        finally { readCancellation = null; busy = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        Enabled = false;
        disposed = true;
        pill.Close();
    }
}
