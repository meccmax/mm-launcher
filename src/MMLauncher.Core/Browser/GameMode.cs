using System.Text.RegularExpressions;

namespace MMLauncher.Core.Browser;

public enum GameMode { Unlabeled, PvE, PvP, Mixed }

/// <summary>
/// DayZ has no PvE setting, so whether a server is PvE is read from what it calls itself:
/// "PvE" or "PVE-ONLY" in the name = PvE; "PvPvE", "PvE/PvP" = mixed; "PvP" = PvP;
/// nothing = unlabeled (plain DayZ, which is PvP).
/// </summary>
public static partial class GameModes
{
    // "pve" / "pvp" as their own word: not inside "pvpve", "pvevp" or another word.
    [GeneratedRegex(@"(?<![a-z])pve(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PvEWord();
    [GeneratedRegex(@"(?<![a-z])pvp(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PvPWord();
    [GeneratedRegex(@"pvpve|pvevp|pve\s*[/&+|-]\s*pvp|pvp\s*[/&+|-]\s*pve", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mixed();

    public static GameMode Classify(string name, string keywords = "")
    {
        var text = name + " " + keywords;
        if (Mixed().IsMatch(text)) return GameMode.Mixed;
        bool pve = PvEWord().IsMatch(text), pvp = PvPWord().IsMatch(text);
        return (pve, pvp) switch
        {
            (true, true) => GameMode.Mixed,
            (true, false) => GameMode.PvE,
            (false, true) => GameMode.PvP,
            _ => GameMode.Unlabeled,
        };
    }

    /// <summary>Short label for the server row ("" when it doesn't say).</summary>
    public static string Label(GameMode m) => m switch
    {
        GameMode.PvE => "PvE",
        GameMode.PvP => "PvP",
        GameMode.Mixed => "PvPvE",
        _ => "",
    };
}
