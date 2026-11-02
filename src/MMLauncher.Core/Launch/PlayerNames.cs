namespace MMLauncher.Core.Launch;

/// <summary>
/// The in-game name passed to DayZ as -name=. DayZ only gets a name from its own launcher, so
/// when another launcher starts it without one, the player shows up as "Survivor".
/// </summary>
public static class PlayerNames
{
    public const int MaxLength = 32;
    public const string DayZDefault = "Survivor";

    /// <summary>Removes characters that break a start parameter or look odd in game, and trims to 32.</summary>
    public static string Clean(string? name)
    {
        var chars = (name ?? "").Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '‮' or '‭' or '‏' or '‎')).ToArray();
        var t = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return t.Length > MaxLength ? t[..MaxLength].TrimEnd() : t;
    }

    /// <summary>A name worth using: at least 2 characters and not DayZ's "Survivor".</summary>
    public static bool IsUsable(string? name)
    {
        var t = Clean(name);
        return t.Length >= 2 && !t.Equals(DayZDefault, StringComparison.OrdinalIgnoreCase);
    }
}
