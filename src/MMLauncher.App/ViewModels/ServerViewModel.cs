using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MMLauncher.Core.Config;
using MMLauncher.Core.Services;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.ViewModels;

/// <summary>One server in the list, plus everything the details panel shows for it.</summary>
public partial class ServerViewModel : ObservableObject
{
    public ServerViewModel(CachedServer cached)
    {
        Cached = cached;
        Name = string.IsNullOrWhiteSpace(cached.LastKnownName) ? $"{cached.Host}:{cached.QueryPort}" : cached.LastKnownName;
        GamePort = cached.GamePort;
    }

    public CachedServer Cached { get; }
    public string Key => $"{Cached.Host}:{Cached.QueryPort}";

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _online;
    [ObservableProperty] private bool _hasStatus;
    [ObservableProperty] private string _map = "";
    [ObservableProperty] private int _players;
    [ObservableProperty] private int _maxPlayers;
    [ObservableProperty] private int? _queue;
    [ObservableProperty] private string _timeOfDay = "";
    [ObservableProperty] private bool _isNight;
    [ObservableProperty] private int _pingMs;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _perspective = "";
    [ObservableProperty] private bool _passworded;
    [ObservableProperty] private string _dlcText = "None";
    [ObservableProperty] private int? _gamePort;
    [ObservableProperty] private string _connectIp = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _modSummary = "";
    [ObservableProperty] private bool _modsMayBeTruncated;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private DateTimeOffset? _lastUpdated;
    [ObservableProperty] private bool _modsFromAgent;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private long _uptimeSeconds;
    [ObservableProperty] private string _modNote = "";
    /// <summary>True once the mod list has been read (from the status reply or an agent), even if it's empty.</summary>
    [ObservableProperty] private bool _modsKnown;
    [ObservableProperty] private DateTimeOffset? _nextRestart;
    [ObservableProperty] private bool _restartApproximate;

    public ObservableCollection<ModViewModel> Mods { get; } = [];

    public string MapLine => string.Join("  ·  ", new[] { Format.MapName(Map), TimeOfDay, Online ? QueueShort : "",
            Online && MinutesToRestart is < 30 ? "restart " + RestartText.Replace("in ", "") : "" }
        .Where(s => !string.IsNullOrEmpty(s)));
    public string PlayersText => Online ? $"{Players}/{MaxPlayers}" : "—";
    public double PlayerFill => MaxPlayers > 0 ? Math.Clamp((double)Players / MaxPlayers, 0, 1) : 0;
    public string QueueShort => Queue is > 0 ? $"queue {Queue}" : "";
    public string QueueText => Queue is { } q ? q.ToString() : "—";
    public string PingText => Online ? $"{PingMs} ms" : HasStatus ? "offline" : "…";
    public bool PingGood => Online && PingMs < 80;
    public bool PingOk => Online && PingMs is >= 80 and < 150;
    public bool PingBad => Online && PingMs >= 150;
    public string TimeText => string.IsNullOrEmpty(TimeOfDay) ? "—" : $"{TimeOfDay} {(IsNight ? "night" : "day")}";
    public string MapText => string.IsNullOrEmpty(Map) ? "—" : Format.MapName(Map);
    public string AddressText => GamePort is { } gp ? $"{Cached.Host}:{gp}" : $"{Cached.Host} (query {Cached.QueryPort})";
    public string ConnectText => GamePort is { } gp && ConnectIp.Length > 0 ? $"{ConnectIp}:{gp}" : AddressText;
    public string PortsText => $"Game port {(GamePort?.ToString() ?? "?")}  ·  Query port {Cached.QueryPort}";
    public string StatusText => !HasStatus ? "Checking…" : Online ? "Online" : "Offline";
    public bool HasError => ErrorText.Length > 0;
    public bool HasMessage => Message.Length > 0;
    public bool HasModNote => ModNote.Length > 0;
    public bool ModsFromServer => HasMods && !ModsFromAgent;
    public bool ShowTruncatedWarning => ModsMayBeTruncated && !ModsFromAgent;
    public string UptimeText => UptimeSeconds <= 0 ? "—" : UptimeSeconds < 60 ? "< 1 min" : UptimeSeconds < 3600
        ? $"{UptimeSeconds / 60} min"
        : UptimeSeconds < 86400 ? $"{UptimeSeconds / 3600}h {UptimeSeconds % 3600 / 60}m" : $"{UptimeSeconds / 86400}d {UptimeSeconds % 86400 / 3600}h";
    // ---- restart schedule (from the MM agent)
    public bool HasRestart => NextRestart != null;
    public double? MinutesToRestart => NextRestart is { } r ? (r - DateTimeOffset.Now).TotalMinutes : null;
    public string RestartText => NextRestart is { } r ? Core.Agent.RestartSchedule.Describe(r, RestartApproximate, DateTimeOffset.Now) : "—";
    public bool RestartSoon => MinutesToRestart is < 15 and > -10;
    public string RestartWarning => MinutesToRestart is < 1
        ? "This server is restarting about now. If you join, you'll probably be kicked within a minute or two."
        : $"This server restarts {RestartText}. If you join now, you'll be kicked when it does.";

    /// <summary>Called every few seconds so countdowns stay current.</summary>
    public void TickClock()
    {
        if (NextRestart == null) return;
        foreach (var n in new[] { nameof(MinutesToRestart), nameof(RestartText), nameof(RestartSoon), nameof(RestartWarning), nameof(MapLine) })
            OnPropertyChanged(n);
    }

    public bool HasMods => Mods.Count > 0;
    public bool WaitingForMods => !HasMods && !ModsKnown;
    public bool IsVanilla => !HasMods && ModsKnown;

    public void Apply(ServerStatus s)
    {
        HasStatus = true;
        Online = s.Online;
        ConnectIp = s.Address?.ToString() ?? ConnectIp;
        // Raw socket messages ("Connection refused", timeouts) mean the same thing to a player.
        ErrorText = !s.Online && s.Error is { } e && !e.StartsWith("Couldn't look up", StringComparison.Ordinal)
            ? "Not answering right now: the server is restarting or down."
            : s.Error ?? "";
        LastUpdated = s.Updated;
        ModNote = "";

        if (s.Online && s.Info != null)
        {
            var info = s.Info;
            Name = info.Name;
            Map = info.Map;
            Players = info.Players;
            MaxPlayers = info.MaxPlayers;
            Version = info.Version;
            Passworded = info.Passworded;
            GamePort = info.GamePort ?? GamePort;
            PingMs = s.PingMs;
            Queue = s.Tags.Queue;
            TimeOfDay = s.Tags.TimeOfDay ?? "";
            IsNight = s.Tags.IsDaytime == false;
            Perspective = s.Tags.FirstPersonOnly ? "1PP only" : "1PP + 3PP";

            if (s.Rules != null)
            {
                Description = s.Rules.Description;
                ModsMayBeTruncated = s.Rules.MayBeTruncated;
                DlcText = s.Rules.RequiredDlcNames.Count > 0 ? string.Join(", ", s.Rules.RequiredDlcNames) : "None";
                if (s.Agent == null) SyncMods(s.Rules.Mods);
                ModsKnown = true;
            }
        }

        // The agent knows the true mod list and full name; it wins over the status reply.
        if (s.Agent is { } agent)
        {
            Name = agent.DisplayName ?? agent.Name;
            Message = agent.Message ?? "";
            UptimeSeconds = agent.UptimeSeconds;
            NextRestart = agent.NextRestart;
            RestartApproximate = agent.RestartApproximate;
            GamePort ??= agent.GamePort;
            if (string.IsNullOrEmpty(Map)) Map = agent.Map;
            if (MaxPlayers == 0) MaxPlayers = agent.MaxPlayers;
            ModsFromAgent = true;
            ModsKnown = true;
            SyncMods(agent.Mods.Select(m => new Core.DayZ.DayZServerMod(m.Id, m.Name, 0)).ToList());

            if (!s.Online)
                ErrorText = "The MM agent says this server is running, but it isn't answering status queries yet (starting up?).";
            else if (s.Rules is { MayBeTruncated: false } rules)
            {
                var a = agent.Mods.Select(m => m.Id).Where(id => id != 0).ToHashSet();
                var r = rules.Mods.Select(m => m.WorkshopId).ToHashSet();
                if (!a.SetEquals(r))
                    ModNote = $"The server's status reply lists {r.Count} mods and the MM agent lists {a.Count}. The agent's list is shown. A restart may be pending.";
            }
        }
        else
        {
            ModsFromAgent = false;
            Message = "";
            UptimeSeconds = 0;
            NextRestart = null;
        }
        RaiseDerived();
    }

    /// <summary>Keeps existing rows (and their workshop info) when the mod list hasn't changed.</summary>
    private void SyncMods(IReadOnlyList<Core.DayZ.DayZServerMod> mods)
    {
        if (Mods.Select(m => m.WorkshopId).SequenceEqual(mods.Select(m => m.WorkshopId))) return;
        var existing = Mods.ToDictionary(m => m.WorkshopId);
        Mods.Clear();
        foreach (var m in mods)
            Mods.Add(existing.TryGetValue(m.WorkshopId, out var vm) ? vm : new ModViewModel(m));
        OnPropertyChanged(nameof(HasMods));
    }

    public void ApplyModDetails(IReadOnlyDictionary<ulong, WorkshopItem> workshop, LocalSteam? steam)
    {
        foreach (var m in Mods)
            m.Apply(workshop.GetValueOrDefault(m.WorkshopId), steam?.Mods.GetValueOrDefault(m.WorkshopId), steam != null);

        _steamKnown = steam != null;
        UpdateModSummary();
    }

    private bool _steamKnown;

    /// <summary>Recounts the summary line (also called after a file check marks mods damaged).</summary>
    public void UpdateModSummary()
    {
        int installed = Mods.Count(m => m.IsInstalled);
        int updates = Mods.Count(m => m.IsUpdate);
        int damaged = Mods.Count(m => m.IsDamaged);
        int missing = Mods.Count(m => m.IsMissing && !m.IsDamaged);
        long total = Mods.Sum(m => m.SizeBytes);
        var parts = new List<string> { $"{Mods.Count} mods", Format.Size(total) };
        if (_steamKnown)
        {
            parts.Add($"{installed} installed");
            if (updates > 0) parts.Add($"{updates} need update");
            if (damaged > 0) parts.Add($"{damaged} damaged");
            if (missing > 0) parts.Add($"{missing} missing");
        }
        ModSummary = string.Join("  ·  ", parts.Where(p => p.Length > 0));
    }

    private void RaiseDerived()
    {
        foreach (var name in new[]
        {
            nameof(MapLine), nameof(PlayersText), nameof(PlayerFill), nameof(QueueShort), nameof(QueueText),
            nameof(PingText), nameof(PingGood), nameof(PingOk), nameof(PingBad), nameof(TimeText), nameof(MapText),
            nameof(AddressText), nameof(ConnectText), nameof(PortsText), nameof(StatusText), nameof(HasError), nameof(HasMods),
            nameof(HasMessage), nameof(HasModNote), nameof(ModsFromServer), nameof(ShowTruncatedWarning), nameof(UptimeText),
            nameof(WaitingForMods), nameof(IsVanilla), nameof(HasRestart), nameof(RestartText), nameof(RestartSoon), nameof(RestartWarning),
        })
            OnPropertyChanged(name);
    }
}
