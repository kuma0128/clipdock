#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:project ../../src/ClipDock.App/ClipDock.App.csproj

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipDock.App;
using Microsoft.Win32;

internal static class Appearance
{
    // Windows sends WM_SETTINGCHANGE with "ImmersiveColorSet" when its app mode changes.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, string lParam,
        uint flags, uint timeout, out nint result);

    [STAThread]
    private static int Main()
    {
        if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The system appearance check runs on a disposable Windows runner.");

        using var settings = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        const string key = "AppsUseLightTheme";
        var original = settings.GetValue(key);
        try
        {
            settings.SetValue(key, 0);
            Application.ResourceAssembly = typeof(App).Assembly;
            var app = new App();
            app.InitializeComponent();
            app.Startup += (_, _) => app.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var main = app.MainWindow;
                    var input = (TextBox)main.FindName("UrlInput");
                    var save = (Button)main.FindName("SaveButton");
                    foreach (var light in new[] { false, true, false })
                    {
                        if ((int)settings.GetValue(key)! != (light ? 1 : 0))
                        {
                            settings.SetValue(key, light ? 1 : 0);
                            await Task.Run(() => SendMessageTimeout(0xffff, 0x001A, 0, "ImmersiveColorSet", 2, 500, out _));
                        }
                        var surface = light ? "#FFFFFF" : "#202722";
                        var accentInk = light ? "#FFFFFF" : "#102B1B";
                        for (var i = 0; i < 50 && !Matches(input.Background, surface); i++) await Task.Delay(100);
                        if (!Matches(input.Background, surface) || !Matches(save.Foreground, accentInk))
                            throw new Exception($"Wrong colours on the open window: {input.Background}, {save.Foreground} (light={light}).");
                        main.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(main);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bitmap));
                        var folder = Directory.CreateDirectory("artifacts/windows-appearance");
                        using var output = File.Create(Path.Combine(folder.FullName, light ? "light.png" : "dark.png"));
                        png.Save(output);
                    }
                    Console.WriteLine("PASS the running Windows app follows dark, light and dark app modes.");
                    app.Shutdown();
                }
                catch (Exception e) { Console.Error.WriteLine(e); app.Shutdown(1); }
            }, DispatcherPriority.Loaded);
            return app.Run();
        }
        finally
        {
            if (original is null) settings.DeleteValue(key, false);
            else settings.SetValue(key, original);
        }
    }

    private static bool Matches(Brush brush, string rgb) =>
        brush is SolidColorBrush solid && solid.Color == (Color)ColorConverter.ConvertFromString(rgb);
}
