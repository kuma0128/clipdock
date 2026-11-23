using System.Windows.Media;

namespace ClipDock.App;

// The window font follows the display language. Yu Gothic UI shows a backslash as a yen sign and draws Chinese
// in Japanese letterforms, so only Japanese starts with it; the other fonts in each list cover any remaining text,
// such as a video title in another script.
internal static class UiFont
{
    internal static FontFamily For(string language) => new(language switch
    {
        "ja" => "Yu Gothic UI, Segoe UI",
        "zh-CN" => "Microsoft YaHei UI, Segoe UI, Yu Gothic UI",
        _ => "Segoe UI, Yu Gothic UI, Microsoft YaHei UI"
    });
}
