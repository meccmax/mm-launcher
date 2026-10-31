using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MMLauncher.App.Views;

public partial class PasswordDialog : Window
{
    public PasswordDialog() => InitializeComponent();

    public PasswordDialog(string serverName) : this()
    {
        ServerName.Text = serverName;
        Opened += (_, _) => PasswordBox.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(PasswordBox.Text ?? "");
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
