using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClipDock.Mac;

// A small modal question. With only an accept label it is a plain notice.
internal static class ConfirmDialog
{
    internal static async Task<bool> ShowAsync(Window owner, string title, string message, string accept, string? decline = null)
    {
        var dialog = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#F8FBF8"))
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 20, 0, 0) };
        if (decline is not null)
        {
            var no = new Button { Content = decline, IsCancel = true };
            no.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(no);
        }
        var yes = new Button
        {
            Content = accept, IsDefault = true, IsCancel = decline is null, FontWeight = FontWeight.SemiBold,
            Background = new SolidColorBrush(Color.Parse("#C6EBCF")), Foreground = new SolidColorBrush(Color.Parse("#132B1D"))
        };
        yes.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(yes);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#20352B")), FontSize = 13, LineHeight = 21 },
                buttons
            }
        };
        return await dialog.ShowDialog<bool?>(owner) == true;
    }
}
