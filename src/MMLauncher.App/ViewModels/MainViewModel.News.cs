using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// The details panel's REQUIRED MODS / NEWS / PLAYERS switch. A server with news you haven't
/// seen opens on NEWS; looking at it marks the posts as seen (they stay highlighted until you
/// move on).
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private string _detailsTab = "mods";

    public bool IsNewsDetails => DetailsTab == "news" && SelectedServer?.HasNews == true;
    public bool IsPlayersDetails => DetailsTab == "players" && SelectedServer?.HasHistory == true;
    public bool IsModsDetails => !IsNewsDetails && !IsPlayersDetails;

    private void RaiseDetailTabs()
    {
        OnPropertyChanged(nameof(IsModsDetails));
        OnPropertyChanged(nameof(IsNewsDetails));
        OnPropertyChanged(nameof(IsPlayersDetails));
        if (IsNewsDetails) SelectedServer?.MarkNewsSeen();
        if (IsPlayersDetails) _ = LoadHistoryAsync();
    }

    partial void OnDetailsTabChanged(string value) => RaiseDetailTabs();

    /// <summary>True once you pick a tab yourself for this server (then news arriving doesn't switch it).</summary>
    private bool _detailsTabPicked;

    [RelayCommand] private void ShowModsDetails() { _detailsTabPicked = true; DetailsTab = "mods"; }
    [RelayCommand] private void ShowNewsDetails() { _detailsTabPicked = true; DetailsTab = "news"; }
    [RelayCommand] private void ShowPlayersDetails() { _detailsTabPicked = true; DetailsTab = "players"; }

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
            ShownHistory = null;
            if (DetailsTab != "players" || newValue?.HasHistory != true)
                DetailsTab = newValue?.HasUnseenNews == true ? "news" : "mods";
        }
        RaiseDetailTabs();
    }

    private void SelectedServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerViewModel.HasHistory)) RaiseDetailTabs();
        if (e.PropertyName is nameof(ServerViewModel.HasNews) or nameof(ServerViewModel.UnseenNews))
        {
            RaiseDetailTabs();
            if (sender is not ServerViewModel { HasUnseenNews: true } s) return;
            // Unseen news arriving for the server you're looking at opens NEWS (unless you picked a tab);
            // new posts arriving while you're reading the news count as seen.
            if (!_detailsTabPicked && !IsSyncing && DetailsTab != "news") DetailsTab = "news";
            else if (IsNewsDetails) s.MarkNewsSeen();
        }
    }
}
