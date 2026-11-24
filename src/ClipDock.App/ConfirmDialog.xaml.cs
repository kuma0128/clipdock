using System.Windows;

namespace ClipDock.App;

public partial class ConfirmDialog : Window
{
    internal ConfirmDialog(Window owner, string title, string message, string accept, string decline)
    {
        InitializeComponent();
        Owner = owner;
        FontFamily = owner.FontFamily;
        Title = title;
        Message.Text = message;
        Accept.Content = accept;
        Decline.Content = decline;
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
