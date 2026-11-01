using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.News;

namespace MMLauncher.App.ViewModels;

/// <summary>One post in a server's NEWS tab.</summary>
public partial class NewsItemViewModel(NewsItem item, bool isNew) : ObservableObject
{
    public NewsItem Item { get; } = item;
    public string Title => Item.Title.Length > 0 ? Item.Title : "News";
    public string Body => Item.Body;
    public bool HasBody => Item.Body.Length > 0;
    public bool Important => Item.Important;
    public bool HasLink => Item.Link.Length > 0;
    public string LinkHost => Uri.TryCreate(Item.Link, UriKind.Absolute, out var u) ? u.Host : "";
    public string LinkText => HasLink ? $"Open link ({LinkHost})" : "";

    public string DateText => Item.Date is { } d
        ? d.ToString(d.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.CurrentCulture)
        : "";
    public bool HasDate => Item.Date != null;

    [ObservableProperty] private bool _isNew = isNew;

    [RelayCommand]
    private void OpenLink()
    {
        // Links were checked to be plain https when they arrived (NewsFormat.SafeLink); check again.
        var link = NewsFormat.SafeLink(Item.Link);
        if (link.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); } catch { }
    }
}
