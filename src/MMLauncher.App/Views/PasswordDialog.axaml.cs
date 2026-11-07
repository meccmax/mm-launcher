using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MMLauncher.App.Views;

/// <summary>What the player typed, and whether to remember it on this PC.</summary>
public sealed record PasswordAnswer(string Password, bool Remember);

public partial class PasswordDialog : Window
{
    public PasswordDialog() => InitializeComponent();

    /// <param name="saved">A password already saved for this server (filled in, "Remember" ticked).</param>
    /// <param name="canSave">False where passwords can't be encrypted, so "Remember" is hidden.</param>
    /// <param name="okText">The main button ("Join", or "Save" when changing a saved password).</param>
    public PasswordDialog(string serverName, string? saved = null, bool canSave = true, string okText = "Join", string? heading = null) : this()
    {
        ServerName.Text = serverName;
        if (heading != null) Heading.Text = heading;
        PasswordBox.Text = saved ?? "";
        // Joining: "Remember" is up to the player. Saving / changing a password: saving is the point, so no checkbox.
        _saveOnly = okText != "Join";
        RememberBox.IsVisible = canSave && !_saveOnly;
        RememberBox.IsChecked = canSave && saved != null;
        OkButton.Content = okText;
        Opened += (_, _) => { PasswordBox.Focus(); PasswordBox.CaretIndex = PasswordBox.Text?.Length ?? 0; };
    }

    private bool _shown;
    private readonly bool _saveOnly;

    private void OnToggleShow(object? sender, RoutedEventArgs e)
    {
        bool show = _shown = !_shown;
        PasswordBox.PasswordChar = show ? '\0' : '•';
        ShowToggle.Content = show ? "Hide" : "Show";
    }

    private void OnOk(object? sender, RoutedEventArgs e) =>
        Close(new PasswordAnswer(PasswordBox.Text ?? "", _saveOnly || (RememberBox.IsVisible && RememberBox.IsChecked == true)));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
