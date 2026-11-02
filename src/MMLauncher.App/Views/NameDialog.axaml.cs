using Avalonia.Controls;
using Avalonia.Interactivity;
using MMLauncher.Core.Launch;

namespace MMLauncher.App.Views;

/// <summary>Asks for the in-game name (DayZ's -name=). Returns the cleaned name, or null for "Later".</summary>
public partial class NameDialog : Window
{
    public NameDialog() => InitializeComponent();

    public NameDialog(string message, string suggested, string okText, string laterText) : this()
    {
        MessageText.Text = message;
        NameBox.Text = suggested;
        OkButton.Content = okText;
        LaterButton.Content = laterText;
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = PlayerNames.Clean(NameBox.Text);
        if (!PlayerNames.IsUsable(name))
        {
            ProblemText.Text = name.Equals(PlayerNames.DayZDefault, StringComparison.OrdinalIgnoreCase)
                ? "That's the name DayZ gives everyone. Pick your own."
                : "Use at least 2 characters.";
            ProblemText.IsVisible = true;
            return;
        }
        Close(name);
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close(null);
}
