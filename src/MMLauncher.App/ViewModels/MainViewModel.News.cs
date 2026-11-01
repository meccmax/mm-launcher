using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// The details panel's REQUIRED MODS / NEWS switch. A server with news you haven't seen opens
/// on NEWS; looking at it marks the posts as seen (they stay highlighted until you move on).
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private string _detailsTab = "mods";

    public bool IsModsDetails => DetailsTab == "mods" || SelectedServer?.HasNews != true;
    public bool IsNewsDetails => !IsModsDetails;

    partial void OnDetailsTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsModsDetails));
        OnPropertyChanged(nameof(IsNewsDetails));
        if (IsNewsDetails) SelectedServer?.MarkNewsSeen();
    }

    /// <summary>True once you pick a tab yourself for this server (then news arriving doesn't switch it).</summary>
    private bool _detailsTabPicked;

    [RelayCommand] private void ShowModsDetails() { _detailsTabPicked = true; DetailsTab = "mods"; }
    [RelayCommand] private void ShowNewsDetails() { _detailsTabPicked = true; DetailsTab = "news"; }

    partial void OnSelectedServerChanged(ServerViewModel? oldValue, ServerViewModel? newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= SelectedServerPropertyChanged;
            oldValue.RefreshNewsSeen();
        }
        if (newValue != null) newValue.PropertyChanged += SelectedServerPropertyChanged;
        // Same server again (a list refresh): keep the tab. Otherwise open NEWS only for unseen news.
        if (oldValue?.Key != newValue?.Key)
        {
            _detailsTabPicked = false;
            DetailsTab = newValue?.HasUnseenNews == true ? "news" : "mods";
        }
        OnPropertyChanged(nameof(IsModsDetails));
        OnPropertyChanged(nameof(IsNewsDetails));
        if (IsNewsDetails) newValue?.MarkNewsSeen();
    }

    private void SelectedServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerViewModel.HasNews) or nameof(ServerViewModel.UnseenNews))
        {
            OnPropertyChanged(nameof(IsModsDetails));
            OnPropertyChanged(nameof(IsNewsDetails));
            if (sender is not ServerViewModel { HasUnseenNews: true } s) return;
            // Unseen news arriving for the server you're looking at opens NEWS (unless you picked a tab);
            // new posts arriving while you're reading the news count as seen.
            if (!_detailsTabPicked && !IsSyncing && DetailsTab != "news") DetailsTab = "news";
            else if (IsNewsDetails) s.MarkNewsSeen();
        }
    }
}
