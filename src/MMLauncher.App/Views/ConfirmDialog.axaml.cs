using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MMLauncher.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string title, string message, string okText) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
