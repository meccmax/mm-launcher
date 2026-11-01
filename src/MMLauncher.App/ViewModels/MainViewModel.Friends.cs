using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Config;
using MMLauncher.Core.Friends;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Query;

namespace MMLauncher.App.ViewModels;

/// <summary>A server in the Friends tab: friends on it now, or seen there before.</summary>
public sealed class FriendServerRow
{
    public required string Server { get; init; }          // "ip:gameport"
    public required string Name { get; init; }
    public required string FriendsText { get; init; }
    public required bool Now { get; init; }
    public string WhenText { get; init; } = "";
    public string Badge => Now ? "PLAYING NOW" : WhenText;
}

/// <summary>
/// The Friends tab: which Steam friends are playing DayZ and where (asked through an MM agent
/// with its Steam Web API key, so the launcher never starts Steam as DayZ), plus servers
/// friends were seen on before (kept on this PC).
/// </summary>
public partial class MainViewModel
{
    private static readonly TimeSpan FriendsEvery = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _friendsTimer = new() { Interval = FriendsEvery };
    private FriendHistory? _friendHistory;
    private List<FriendInfo> _friendsNow = [];
    private bool _friendsTimerHooked;

    [ObservableProperty] private string _friendsStatus = "";
    [ObservableProperty] private bool _friendsLoading;
    [ObservableProperty] private FriendServerRow? _selectedFriendRow;
    [ObservableProperty] private string _friendsInMenus = "";

    public ObservableCollection<FriendServerRow> FriendRows { get; } = [];
    public bool IsFriendsTab => ListTab == "friends";
    public bool NoFriendRows => IsFriendsTab && FriendRows.Count == 0 && !FriendsLoading;
    public bool HasFriendsInMenus => FriendsInMenus.Length > 0;
    partial void OnFriendsInMenusChanged(string value) => OnPropertyChanged(nameof(HasFriendsInMenus));
    partial void OnFriendsLoadingChanged(bool value) => OnPropertyChanged(nameof(NoFriendRows));

    /// <summary>Friends on the server shown in the details panel ("" = none known).</summary>
    public string FriendsOnSelected
    {
        get
        {
            if (SelectedServer is not { GamePort: { } gp } s || s.ConnectIp.Length == 0) return "";
            var names = _friendsNow.Where(f => f.Server == $"{s.ConnectIp}:{gp}").Select(f => f.Name).ToList();
            return names.Count == 0 ? "" : "Friends here: " + string.Join(", ", names);
        }
    }
    public bool HasFriendsOnSelected => FriendsOnSelected.Length > 0;

    [RelayCommand]
    private async Task ShowFriendsTabAsync()
    {
        ListTab = "friends";
        if (!_friendsTimerHooked)
        {
            _friendsTimerHooked = true;
            _friendsTimer.Tick += async (_, _) => { if (IsFriendsTab) await RefreshFriendsAsync(); };
        }
        _friendsTimer.Start();
        await RefreshFriendsAsync();
    }

    [RelayCommand]
    private async Task RefreshFriendsAsync()
    {
        if (FriendsLoading) return;
        FriendsLoading = true;
        try
        {
            _friendHistory ??= FriendHistory.Load();
            DetectSteam();
            var me = SteamAccount.Current(_steam?.SteamPath);
            if (me == null)
            {
                FriendsStatus = "Couldn't tell which Steam account you're signed in to. Start Steam and sign in, then try again.";
                return;
            }

            // The agents with a Steam Web API key (the same ones that share the server list).
            if (_service.LastAgents.BrowserAgents.Count == 0) await _service.QueryAgentsAsync();
            FriendsReply? reply = null;
            foreach (var (host, port) in _service.LastAgents.BrowserAgents)
                if ((reply = await _service.Agents.GetFriendsAsync(host, port, me.Value)) != null) break;

            if (reply == null)
            {
                FriendsStatus = "No MM server agent is offering the Friends feature right now. Showing servers friends were seen on before.";
            }
            else if (!reply.Ok)
            {
                FriendsStatus = reply.Message;
            }
            else
            {
                _friendsNow = reply.InDayZ;
                // Name lookups need the big server list; load it if we don't have it yet.
                if (Browser.Feed == null) Browser.LoadCache();
                if (Browser.Feed == null || Browser.IsStale) await Browser.RefreshAsync();
                _friendHistory.Record(_friendsNow, ServerNameFor, DateTimeOffset.Now);
                _friendHistory.Save();
                int playing = _friendsNow.Count;
                int onServers = _friendsNow.Count(f => f.Server.Length > 0);
                FriendsStatus = playing == 0
                    ? $"None of your {reply.FriendCount} Steam friends are playing DayZ right now."
                    : $"{playing} of your {reply.FriendCount} friends are playing DayZ" + (onServers < playing ? $" ({playing - onServers} not on a server, or with private game details)." : ".");
                var menus = _friendsNow.Where(f => f.Server.Length == 0).Select(f => f.Name).ToList();
                FriendsInMenus = menus.Count == 0 ? "" : "In DayZ, server not shown: " + string.Join(", ", menus);
                OnPropertyChanged(nameof(FriendsOnSelected));
                OnPropertyChanged(nameof(HasFriendsOnSelected));
            }
            RebuildFriendRows();
        }
        catch (Exception ex)
        {
            Log.Warn("Friends refresh failed: " + ex.Message);
            FriendsStatus = "Couldn't get your friends right now: " + ex.Message;
        }
        finally
        {
            FriendsLoading = false;
        }
    }

    private void RebuildFriendRows()
    {
        var keep = SelectedFriendRow?.Server;
        FriendRows.Clear();
        foreach (var g in _friendsNow.Where(f => f.Server.Length > 0).GroupBy(f => f.Server).OrderByDescending(g => g.Count()))
            FriendRows.Add(new FriendServerRow
            {
                Server = g.Key, Name = ServerNameFor(g.Key) ?? g.Key, Now = true,
                FriendsText = string.Join(", ", g.Select(f => f.Name)),
            });
        var now = DateTimeOffset.Now;
        foreach (var s in _friendHistory?.Servers ?? [])
        {
            if (FriendRows.Any(r => r.Server == s.Server)) continue;
            FriendRows.Add(new FriendServerRow
            {
                Server = s.Server, Name = s.ServerName.Length > 0 ? s.ServerName : s.Server, Now = false,
                FriendsText = string.Join(", ", s.Friends.Take(6)) + (s.Friends.Count > 6 ? $" +{s.Friends.Count - 6}" : ""),
                WhenText = "seen " + Format.Ago(s.LastSeen),
            });
        }
        SelectedFriendRow = FriendRows.FirstOrDefault(r => r.Server == keep);
        OnPropertyChanged(nameof(NoFriendRows));
    }

    /// <summary>A server's name from the MM list or the big server list, by "ip:gameport".</summary>
    private string? ServerNameFor(string server)
    {
        var mm = Servers.FirstOrDefault(s => s.GamePort is { } gp && $"{s.ConnectIp}:{gp}" == server);
        if (mm != null) return mm.Name;
        return FeedEntryFor(server)?.Name;
    }

    private Core.Browser.BrowserEntry? FeedEntryFor(string server)
    {
        if (Browser.Feed is not { } feed) return null;
        int colon = server.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(server.AsSpan(colon + 1), out var gamePort)) return null;
        var ip = server[..colon];
        return feed.Servers.FirstOrDefault(e => e.GamePort == gamePort && e.Address.StartsWith(ip + ":", StringComparison.Ordinal));
    }

    partial void OnSelectedFriendRowChanged(FriendServerRow? value)
    {
        if (value != null) _ = OpenFriendServerAsync(value);
    }

    /// <summary>Opens a friend's server in the details panel. Steam only gives its game port,
    /// so the query port comes from the server lists, or by asking the usual ports.</summary>
    private async Task OpenFriendServerAsync(FriendServerRow row)
    {
        int colon = row.Server.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(row.Server.AsSpan(colon + 1), out var gamePort)) return;
        var ip = row.Server[..colon];

        var mm = Servers.FirstOrDefault(s => s.ConnectIp == ip && s.GamePort == gamePort);
        if (mm != null) { SelectedServer = mm; return; }

        int? queryPort = null;
        if (FeedEntryFor(row.Server) is { } e && e.TrySplit(out _, out var qp)) queryPort = qp;
        queryPort ??= await ProbeQueryPortAsync(ip, gamePort);
        if (queryPort == null)
        {
            if (!IsSyncing) ShowResult(false, $"{row.Name} isn't answering",
                $"Friends were seen on {row.Server}, but it isn't answering status queries right now. It may be down or restarting, or not allow queries.");
            return;
        }
        var vm = new ServerViewModel(new CachedServer { Host = ip, QueryPort = queryPort.Value, GamePort = gamePort, LastKnownName = row.Name });
        SelectedServer = vm;
        await RefreshOneAsync(vm);
    }

    private static async Task<int?> ProbeQueryPortAsync(string ip, int gamePort)
    {
        if (!IPAddress.TryParse(ip, out var address)) return null;
        // DayZ's default is 27016; many hosts use game port + 1 (or + 2, + 3).
        var candidates = new[] { gamePort + 1, 27016, gamePort + 2, gamePort + 3, gamePort, 27015 }.Where(p => p is > 0 and <= 65535).Distinct();
        var tasks = candidates.Select(async p =>
        {
            try
            {
                var (info, _) = await A2sClient.QueryInfoAsync(new IPEndPoint(address, p), TimeSpan.FromSeconds(2));
                return info.GamePort == null || info.GamePort == gamePort ? p : (int?)null;
            }
            catch { return (int?)null; }
        }).ToList();
        foreach (var r in await Task.WhenAll(tasks)) if (r != null) return r;
        return null;
    }
}
