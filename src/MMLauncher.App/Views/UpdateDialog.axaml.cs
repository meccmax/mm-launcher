using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MMLauncher.Core.Updates;

namespace MMLauncher.App.Views;

/// <summary>"Update to vX?" with the release notes laid out properly (headings, bullets) and scrollable.</summary>
public partial class UpdateDialog : Window
{
    private string _notesUrl = UpdateChecker.ReleasesPage;

    public UpdateDialog() => InitializeComponent();

    public UpdateDialog(string title, string message, IReadOnlyList<NoteLine> notes, string notesUrl) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        _notesUrl = notesUrl;
        NotesBox.IsVisible = notes.Count > 0;
        foreach (var line in notes) NotesPanel.Children.Add(Render(line));
    }

    private Control Render(NoteLine line) => line.Kind switch
    {
        NoteKind.Version => Text(line.Text, 13, FontWeight.Bold, "Orange", new(0, 4, 0, 2)),
        NoteKind.Heading => Text(line.Text, 14, FontWeight.SemiBold, "Text", new(0, 6, 0, 2)),
        NoteKind.Gap => new Border { Height = 6 },
        NoteKind.Bullet => Bullet(line.Text),
        _ => Text(line.Text, 13, FontWeight.Normal, "Silver", new(0, 1)),
    };

    private TextBlock Text(string text, double size, FontWeight weight, string brush, Avalonia.Thickness margin)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
        return t;
    }

    private Control Bullet(string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*"), Margin = new(4, 1, 0, 1) };
        var dot = Text("•", 13, FontWeight.Normal, "Orange", new(0));
        dot.VerticalAlignment = VerticalAlignment.Top;
        var body = Text(text, 13, FontWeight.Normal, "Silver", new(0));
        Grid.SetColumn(body, 1);
        grid.Children.Add(dot);
        grid.Children.Add(body);
        return grid;
    }

    private void OnNotesLink(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_notesUrl) { UseShellExecute = true }); } catch { }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
