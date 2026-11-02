using CommunityToolkit.Mvvm.ComponentModel;
using MMLauncher.Core.Browser;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Query;

namespace MMLauncher.App.ViewModels;

/// <summary>One server in the "All servers" list.</summary>
public partial class BrowserRowViewModel : ObservableObject
{
    public BrowserRowViewModel(BrowserEntry entry)
    {
        Entry = entry;
        Name = entry.Name;
        Players = entry.Players;
        MaxPlayers = entry.MaxPlayers;
        var tags = DayZTags.Parse(entry.Keywords);
        Perspective = tags.FirstPersonOnly ? "1PP" : "3PP";
        TimeOfDay = tags.TimeOfDay ?? "";
        Queue = tags.Queue;
        Country = entry.Country.ToUpperInvariant();
        Mode = GameModes.Label(GameModes.Classify(entry.Name, entry.Keywords));
    }

    public BrowserEntry Entry { get; }
    public string Key => Entry.Address;

    [ObservableProperty] private string _name;
    [ObservableProperty] private int _players;
    [ObservableProperty] private int _maxPlayers;
    [ObservableProperty] private int? _pingMs;          // null = not measured yet, -1 = no answer
    [ObservableProperty] private bool _passworded;
    /// <summary>When a search matched this server's mods rather than its name: "runs TerjeCore, TerjeSkills".</summary>
    [ObservableProperty] private string _modHint = "";
    public bool HasModHint => ModHint.Length > 0;
    partial void OnModHintChanged(string value) => OnPropertyChanged(nameof(HasModHint));

    /// <summary>How many mods it runs ("" if not known), and whether one of them is an AI mod.</summary>
    public string ModCountText { get; private set; } = "";
    public bool RunsAi { get; private set; }

    public void ApplyModIndex(BrowserModIndex? index)
    {
        int count = index?.ModsOf(Entry.Address).Count ?? 0;
        ModCountText = count > 0 ? $"{count} mods" : "";
        RunsAi = BrowserModIndex.NameSaysAi(Entry.Name) || index?.RunsAi(Entry.Address) == true;
        OnPropertyChanged(nameof(ModCountText));
        OnPropertyChanged(nameof(RunsAi));
        OnPropertyChanged(nameof(MapLine));
    }
    public string Perspective { get; }
    public string TimeOfDay { get; }
    public int? Queue { get; }
    /// <summary>"US", "DE", … ("" when unknown).</summary>
    public string Country { get; }
    public string CountryName => Countries.Name(Country);
    public bool HasCountry => Country.Length > 0;
    /// <summary>"PvE", "PvP", "PvPvE" when the server's name says so.</summary>
    public string Mode { get; }

    public string MapLine => string.Join("  ·  ", new[]
    {
        Format.MapName(Entry.Map), Perspective, Mode, RunsAi ? "AI" : "", ModCountText, TimeOfDay, Queue is > 0 ? $"queue {Queue}" : "", Passworded ? "password" : "",
    }.Where(p => p.Length > 0));

    public string PlayersText => $"{Players}/{MaxPlayers}";
    public double PlayerFill => MaxPlayers > 0 ? Math.Clamp((double)Players / MaxPlayers, 0, 1) : 0;
    public string PingText => PingMs switch { null => "…", < 0 => "no reply", var p => $"{p} ms" };
    public bool PingGood => PingMs is >= 0 and < 80;
    public bool PingOk => PingMs is >= 80 and < 150;
    public bool PingBad => PingMs is >= 150; // "no reply" stays neutral: many servers just don't answer pings

    /// <summary>A fresh status reply: real player count, password flag and ping.</summary>
    public void ApplyPing(int ms, A2sInfo? info)
    {
        PingMs = ms;
        if (info != null)
        {
            Players = info.Players;
            MaxPlayers = info.MaxPlayers;
            Passworded = info.Passworded;
        }
        foreach (var n in new[] { nameof(PingText), nameof(PingGood), nameof(PingOk), nameof(PingBad), nameof(PlayersText), nameof(PlayerFill), nameof(MapLine) })
            OnPropertyChanged(n);
    }
}
