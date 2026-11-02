using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Browser;
using MMLauncher.Core.Config;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.ViewModels;

/// <summary>A choice in a filter drop-down.</summary>
public sealed record FilterOption(string Label, string? Value);

/// <summary>
/// The left panel's tabs: MM servers, All servers (every DayZ server, via an MM agent),
/// Favorites and Recent. Any server picked in any tab opens in the same details panel,
/// so mod sync, JOIN and auto-rejoin work the same everywhere.
/// </summary>
public partial class MainViewModel
{
    private BrowserService? _browserService;
    private BrowserService Browser => _browserService ??= new BrowserService(_service);
    private readonly ServerLists _lists = ServerLists.Load();
    private Dictionary<string, BrowserRowViewModel> _rows = new();
    private CancellationTokenSource? _pingCts;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _browserLoaded;

    [ObservableProperty] private string _listTab = "mm";
    [ObservableProperty] private IReadOnlyList<BrowserRowViewModel> _browserRows = [];
    [ObservableProperty] private BrowserRowViewModel? _selectedBrowserRow;
    [ObservableProperty] private ServerViewModel? _selectedMmServer;
    [ObservableProperty] private ServerViewModel? _selectedSavedServer;
    [ObservableProperty] private string _browserStatus = "";
    [ObservableProperty] private bool _browserLoading;
    [ObservableProperty] private string _browserSearch = "";
    [ObservableProperty] private IReadOnlyList<FilterOption> _mapOptions = [new("All maps", null)];
    [ObservableProperty] private FilterOption? _selectedMap;
    [ObservableProperty] private int _perspectiveIndex;
    [ObservableProperty] private int _modsIndex;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private bool _hideFull;
    [ObservableProperty] private IReadOnlyList<FilterOption> _countryOptions = [new("Anywhere", null)];
    [ObservableProperty] private FilterOption? _selectedCountry;
    [ObservableProperty] private int _playerCountIndex = (int)PlayerRange.NotEmpty;
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private bool _hasAi;

    public ObservableCollection<ServerViewModel> FavoriteServers { get; } = [];
    public ObservableCollection<ServerViewModel> RecentServers { get; } = [];

    public IReadOnlyList<string> PerspectiveChoices { get; } = ["1PP and 3PP", "1PP only", "3PP allowed"];
    public IReadOnlyList<string> ModsChoices { get; } = ["Modded and vanilla", "Modded only", "Vanilla only"];
    public IReadOnlyList<string> SortChoices { get; } = ["Most players", "Name", "Map", "Lowest ping"];
    public IReadOnlyList<string> PlayerCountChoices { get; } = ["Any player count", "Has players", "1–14 players", "15–49 players", "50+ players"];
    public IReadOnlyList<string> ModeChoices { get; } = ["PvE and PvP", "PvE only", "PvP / PvPvE"];
    public string BrowserSearchHint => Browser.Mods != null ? "Search name, map, mod or IP address" : "Search name, map or IP address";
    public string HasAiTip => Browser.Mods != null
        ? "Servers running an AI mod (DayZ-Expansion-AI, the Expansion Bundle, or a mod with AI or Bandits in its name), or with AI in their name"
        : "Servers with AI in their name. (The MM agent hasn't sent mod lists yet, so AI mods can't be checked.)";
    public string CountryTip =>
        "Where each server is hosted, from its IP address. Usually where its players are, but not always.\n" + GeoIpDatabase.Attribution;

    public bool IsMmTab => ListTab == "mm";
    public bool IsAllTab => ListTab == "all";
    public bool IsFavoritesTab => ListTab == "fav";
    public bool IsRecentTab => ListTab == "recent";
    public bool IsSavedTab => IsFavoritesTab || IsRecentTab;
    public bool NoFavorites => IsFavoritesTab && FavoriteServers.Count == 0;
    public bool NoRecent => IsRecentTab && RecentServers.Count == 0;
    public IEnumerable<ServerViewModel> SavedServers => IsFavoritesTab ? FavoriteServers : RecentServers;

    public bool SelectedIsFavorite => SelectedServer is { } s && _lists.IsFavorite(s.Cached.Host, s.Cached.QueryPort);
    public string FavoriteText => SelectedIsFavorite ? "★ Favorite" : "☆ Favorite";

    partial void OnListTabChanged(string value)
    {
        foreach (var n in new[] { nameof(IsMmTab), nameof(IsAllTab), nameof(IsFavoritesTab), nameof(IsRecentTab), nameof(IsSavedTab),
                                  nameof(IsFriendsTab), nameof(NoFriendRows), nameof(NoFavorites), nameof(NoRecent), nameof(SavedServers) })
            OnPropertyChanged(n);
        if (value != "friends") _friendsTimer.Stop();
    }

    [RelayCommand] private void ShowMmTab() { ListTab = "mm"; if (SelectedMmServer != null) SelectedServer = SelectedMmServer; }
    [RelayCommand] private async Task ShowAllTabAsync() { ListTab = "all"; await EnsureBrowserAsync(); }
    [RelayCommand] private async Task ShowFavoritesTabAsync() { ListTab = "fav"; await LoadSavedAsync(FavoriteServers, _lists.Favorites); }
    [RelayCommand] private async Task ShowRecentTabAsync() { ListTab = "recent"; await LoadSavedAsync(RecentServers, _lists.Recent); }

    // ------------------------------------------------------------------ selection

    private bool _quietMmSelect;

    partial void OnSelectedMmServerChanged(ServerViewModel? value)
    {
        if (value != null && !_quietMmSelect) SelectedServer = value;
    }

    partial void OnSelectedSavedServerChanged(ServerViewModel? value)
    {
        if (value == null) return;
        SelectedServer = value;
        _ = RefreshOneAsync(value);
    }

    partial void OnSelectedBrowserRowChanged(BrowserRowViewModel? value)
    {
        if (value == null || !value.Entry.TrySplit(out var ip, out var port)) return;
        // An MM server picked in the big list opens as the MM entry (agent data and all).
        var mm = Servers.FirstOrDefault(s => s.ConnectIp == ip && s.Cached.QueryPort == port);
        var vm = mm ?? new ServerViewModel(new CachedServer { Host = ip, QueryPort = port, GamePort = value.Entry.GamePort, LastKnownName = value.Name });
        SelectedServer = vm;
        if (mm == null) _ = RefreshOneAsync(vm);
    }

    /// <summary>Re-queries one server (status, mod list, workshop details) and updates it.</summary>
    private async Task RefreshOneAsync(ServerViewModel vm)
    {
        try
        {
            var status = await _service.RefreshAsync(vm.Cached);
            var agent = _service.LastAgents.Find(status.Address, vm.Cached.QueryPort);
            vm.Apply(status with { Agent = agent });
            var ids = vm.Mods.Select(m => m.WorkshopId).Where(id => id != 0).Distinct().ToList();
            IReadOnlyDictionary<ulong, WorkshopItem> workshop = new Dictionary<ulong, WorkshopItem>();
            if (ids.Count > 0)
            {
                try { workshop = await _service.Workshop.GetDetailsAsync(ids); }
                catch (Exception ex) { Log.Warn("Workshop lookup failed: " + ex.Message); }
            }
            vm.ApplyModDetails(workshop, _steam);
            _lists.RememberMods(vm.Cached.Host, vm.Cached.QueryPort, vm.Mods.Select(m => m.WorkshopId));
            if (ReferenceEquals(vm, SelectedServer)) RaiseJoinState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Refreshing {vm.Name} failed: {ex.Message}");
        }
    }

    /// <summary>True when this server is one of the MM list's own entries.</summary>
    private bool IsMmEntry(ServerViewModel vm) => Servers.Contains(vm);

    // ------------------------------------------------------------------ favorites / recent

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (SelectedServer is not { } s) return;
        bool now = _lists.ToggleFavorite(s.Cached.Host, s.Cached.QueryPort, s.GamePort, s.Name);
        if (now) _lists.RememberMods(s.Cached.Host, s.Cached.QueryPort, s.Mods.Select(m => m.WorkshopId));
        StatusMessage = now ? $"Added {s.Name} to favorites" : $"Removed {s.Name} from favorites";
        OnPropertyChanged(nameof(SelectedIsFavorite));
        OnPropertyChanged(nameof(FavoriteText));
        if (IsFavoritesTab) _ = LoadSavedAsync(FavoriteServers, _lists.Favorites);
    }

    private void RememberRecent(ServerViewModel s)
    {
        _lists.AddRecent(s.Cached.Host, s.Cached.QueryPort, s.GamePort, s.Name);
        _lists.RememberMods(s.Cached.Host, s.Cached.QueryPort, s.Mods.Select(m => m.WorkshopId));
        if (IsRecentTab) _ = LoadSavedAsync(RecentServers, _lists.Recent);
    }

    private async Task LoadSavedAsync(ObservableCollection<ServerViewModel> target, List<SavedServer> saved)
    {
        var keep = target.ToDictionary(v => v.Key);
        target.Clear();
        foreach (var s in saved)
            target.Add(keep.GetValueOrDefault(s.Key) ?? new ServerViewModel(new CachedServer
            {
                Host = s.Host, QueryPort = s.QueryPort, GamePort = s.GamePort, LastKnownName = s.Name,
            }));
        OnPropertyChanged(nameof(NoFavorites));
        OnPropertyChanged(nameof(NoRecent));
        await Task.WhenAll(target.ToList().Select(RefreshOneAsync));
    }

    // ------------------------------------------------------------------ all servers

    private async Task EnsureBrowserAsync()
    {
        if (!_browserLoaded)
        {
            _browserLoaded = true;
            _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyBrowserFilter(); };
            Browser.LoadCache();
            RaiseModSearchState();
            if (Browser.Feed != null) RebuildRows();
        }
        if (Browser.IsStale) await RefreshBrowserAsync();
        else if (Browser.ModsStale) _ = RefreshModListsAsync();
    }

    [RelayCommand]
    private async Task RefreshBrowserAsync()
    {
        if (BrowserLoading) return;
        BrowserLoading = true;
        BrowserStatus = Browser.Feed == null ? "Getting the server list…" : BrowserStatus;
        try
        {
            if (await Browser.RefreshAsync()) RebuildRows();
            else UpdateBrowserStatus(Browser.LastError);
            if (Browser.ModsStale) _ = RefreshModListsAsync();
        }
        finally
        {
            BrowserLoading = false;
        }
    }

    /// <summary>Gets the mod lists (for mod search and the AI filter) in the background, then re-filters.</summary>
    private async Task RefreshModListsAsync()
    {
        try
        {
            if (await Browser.RefreshModsAsync())
            {
                foreach (var row in _rows.Values) row.ApplyModIndex(Browser.Mods);
                RaiseModSearchState();
                ApplyBrowserFilter();
            }
        }
        catch (Exception ex) { Log.Warn("Mod lists: " + ex.Message); }
    }

    private void RaiseModSearchState()
    {
        OnPropertyChanged(nameof(BrowserSearchHint));
        OnPropertyChanged(nameof(HasAiTip));
    }

    private void RebuildRows()
    {
        var feed = Browser.Feed;
        if (feed == null) return;
        var old = _rows;
        _rows = new Dictionary<string, BrowserRowViewModel>(feed.Servers.Count);
        foreach (var e in feed.Servers)
        {
            var row = new BrowserRowViewModel(e);
            row.ApplyModIndex(Browser.Mods);
            if (old.TryGetValue(e.Address, out var prev) && prev.PingMs is { } ms) row.PingMs = ms;
            _rows[e.Address] = row;
        }
        var previous = SelectedMap?.Value;
        MapOptions = [new FilterOption("All maps", null), .. BrowserService.Maps(feed.Servers).Select(m => new FilterOption($"{Format.MapName(m.Map)}  ({m.Count})", m.Map))];
        SelectedMap = MapOptions.FirstOrDefault(m => m.Value == previous) ?? MapOptions[0];
        var previousCountry = SelectedCountry?.Value;
        CountryOptions = [new FilterOption("Anywhere", null), .. BrowserService.CountryChoices(feed.Servers).Select(c => new FilterOption($"{c.Label}  ({c.Count:N0})", c.Value))];
        SelectedCountry = CountryOptions.FirstOrDefault(c => c.Value == previousCountry) ?? CountryOptions[0];
        ApplyBrowserFilter();
    }

    partial void OnBrowserSearchChanged(string value) { _searchDebounce.Stop(); _searchDebounce.Start(); }
    partial void OnSelectedMapChanged(FilterOption? value) => ApplyBrowserFilter();
    partial void OnPerspectiveIndexChanged(int value) => ApplyBrowserFilter();
    partial void OnModsIndexChanged(int value) => ApplyBrowserFilter();
    partial void OnSortIndexChanged(int value) => ApplyBrowserFilter();
    partial void OnHideFullChanged(bool value) => ApplyBrowserFilter();
    partial void OnSelectedCountryChanged(FilterOption? value) => ApplyBrowserFilter();
    partial void OnPlayerCountIndexChanged(int value) => ApplyBrowserFilter();
    partial void OnModeIndexChanged(int value) => ApplyBrowserFilter();
    partial void OnHasAiChanged(bool value) => ApplyBrowserFilter();

    private BrowserFilter CurrentFilter() => new(
        BrowserSearch,
        SelectedMap?.Value,
        (PerspectiveFilter)Math.Clamp(PerspectiveIndex, 0, 2),
        false,
        HideFull,
        (ModFilter)Math.Clamp(ModsIndex, 0, 2),
        (BrowserSort)Math.Clamp(SortIndex, 0, 3),
        (PlayerRange)Math.Clamp(PlayerCountIndex, 0, 4),
        (ModeFilter)Math.Clamp(ModeIndex, 0, 2),
        SelectedCountry?.Value,
        HasAi);

    private void ApplyBrowserFilter()
    {
        if (Browser.Feed is not { } feed) return;
        var filter = CurrentFilter();
        var list = BrowserService.Apply(feed.Servers, filter, e => _rows.TryGetValue(e.Address, out var r) ? r.PingMs : null, Browser.Mods);
        BrowserRows = list.Select(e => _rows[e.Address]).ToList();
        ShowModMatches(BrowserRows, filter.Search);
        UpdateBrowserStatus(null);

        // Ping what's near the top of the list (all of it, up to a limit, when sorting by ping).
        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        var targets = list.Take(filter.Sort == BrowserSort.Ping ? 600 : 150).ToList();
        _ = PingRowsAsync(targets, resortWhenDone: filter.Sort == BrowserSort.Ping, _pingCts.Token);
    }

    private async Task PingRowsAsync(List<BrowserEntry> targets, bool resortWhenDone, CancellationToken ct)
    {
        await Browser.PingAsync(targets, (entry, ms, info) =>
            Dispatcher.UIThread.Post(() => { if (_rows.TryGetValue(entry.Address, out var row)) row.ApplyPing(ms, info); }), ct);
        if (resortWhenDone && !ct.IsCancellationRequested)
            Dispatcher.UIThread.Post(() =>
            {
                if (Browser.Feed is not { } feed || CurrentFilter().Sort != BrowserSort.Ping) return;
                var list = BrowserService.Apply(feed.Servers, CurrentFilter(), e => _rows.TryGetValue(e.Address, out var r) ? r.PingMs : null, Browser.Mods);
                BrowserRows = list.Select(e => _rows[e.Address]).ToList();
            });
    }

    /// <summary>
    /// For servers found by a mod (not by their name), shows which of their mods matched,
    /// e.g. "runs TerjeCore, TerjeMedicine +2".
    /// </summary>
    private void ShowModMatches(IReadOnlyList<BrowserRowViewModel> rows, string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var row in rows)
        {
            string hint = "";
            if (words.Length > 0 && Browser.Mods is { } mods)
            {
                var matched = words.Where(w => !row.Entry.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                            && !row.Entry.Map.Contains(w, StringComparison.OrdinalIgnoreCase)
                                            && !row.Entry.Address.StartsWith(w, StringComparison.OrdinalIgnoreCase))
                                   .SelectMany(w => mods.MatchingMods(row.Entry.Address, w)).Distinct().ToList();
                if (matched.Count > 0)
                    hint = "runs " + string.Join(", ", matched.Take(3)) + (matched.Count > 3 ? $" +{matched.Count - 3}" : "");
            }
            row.ModHint = hint;
        }
    }

    private void UpdateBrowserStatus(string? error)
    {
        var total = Browser.Feed?.Servers.Count ?? 0;
        var age = Browser.FetchedAt is { } t ? (DateTimeOffset.Now - t) : (TimeSpan?)null;
        var ageText = age switch
        {
            null => "",
            { TotalMinutes: < 1 } => "  ·  updated just now",
            { TotalMinutes: < 90 } a => $"  ·  updated {(int)a.TotalMinutes} min ago",
            var a => $"  ·  updated {(int)a.Value.TotalHours} h ago",
        };
        BrowserStatus = total == 0
            ? error ?? "No server list yet."
            : $"{BrowserRows.Count:N0} of {total:N0} servers{ageText}" + (error != null ? $"  ·  {error}" : "");
    }
}
