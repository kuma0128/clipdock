using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClipDock.Core;

namespace ClipDock.Mac;

// The menu bar icon. Its menu opens or quits ClipDock; macOS shows the menu on any click.
// (In the Avalonia build on Windows it is a notification-area icon, and a click opens the window.)
// Registered with the application, whose lifetime removes it when the process ends.
internal sealed class MenuBarIcon
{
    private readonly TrayIcon icon;

    internal MenuBarIcon(Action open, Action quit)
    {
        var openItem = new NativeMenuItem(Localization.Text("ClipDockを開く"));
        openItem.Click += (_, _) => open();
        var quitItem = new NativeMenuItem(Localization.Text("ClipDockを終了"));
        quitItem.Click += (_, _) => quit();
        icon = new TrayIcon { Icon = Glyph(), ToolTipText = "ClipDock", Menu = new NativeMenu { openItem, new NativeMenuItemSeparator(), quitItem } };
        // A template image: macOS draws it in the menu bar's own colour, light or dark.
        MacOSProperties.SetIsTemplateIcon(icon, true);
        icon.Clicked += (_, _) => open();
        TrayIcon.SetIcons(Application.Current!, [icon]);
    }

    internal bool IsVisible => icon.IsVisible;

    internal string Text
    {
        get => icon.ToolTipText ?? "";
        set { if (icon.ToolTipText != value) icon.ToolTipText = value; }
    }

    // An arrow into a tray, drawn at 36 px for the 18 pt menu bar. Only the shape matters for a template image.
    private static WindowIcon Glyph()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawGeometry(Brushes.Black, null, Geometry.Parse("M15.5,4 H20.5 V16 H26.5 L18,25.5 L9.5,16 H15.5 Z"));
            context.DrawGeometry(null, new Pen(Brushes.Black, 3.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Geometry.Parse("M5.5,21 V30.5 H30.5 V21"));
        }
        using var png = new MemoryStream();
        bitmap.Save(png, new PngBitmapEncoderOptions());
        png.Position = 0;
        return new WindowIcon(png);
    }
}
