using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MMLauncher.Core.Config;
using MMLauncher.Core.Logging;

namespace MMLauncher.Core.Launch;

/// <summary>
/// "Game display" in Settings: start DayZ fullscreen or in a window of a chosen size.
/// DayZ keeps these in Documents\DayZ\DayZ.cfg (Windowed=0/1; WindowWidth / WindowHeight for the
/// window), which it reads at start and rewrites when it closes. Just before launching, the
/// launcher sets those keys to the player's choice and leaves every other line exactly as it was.
/// The first time it changes the file it keeps a copy as DayZ.cfg.mmlauncher-backup.
/// Fullscreen always uses the desktop resolution (DayZ's own rule); the in-game "rendering
/// resolution" setting scales it.
/// </summary>
public static partial class GameDisplay
{
    public const string Keep = "dayz";
    public const string Fullscreen = "fullscreen";
    public const string Windowed = "windowed";

    public const int MinSize = 640;
    public const int MaxSize = 7680;

    /// <summary>Documents\DayZ\DayZ.cfg (MMLAUNCHER_DAYZ_CFG overrides it, for testing).</summary>
    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("MMLAUNCHER_DAYZ_CFG") is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DayZ", "DayZ.cfg");

    public static string BackupPath(string cfg) => cfg + ".mmlauncher-backup";

    /// <summary>What DayZ.cfg says now: windowed or not, and the window size (0 when not set).</summary>
    public static (bool? Windowed, int Width, int Height) Read(string? path = null)
    {
        path ??= ConfigPath;
        try
        {
            if (!File.Exists(path)) return (null, 0, 0);
            var text = File.ReadAllText(path);
            bool? windowed = Value(text, "Windowed") is { } w ? w != 0 : null;
            return (windowed, Value(text, "WindowWidth") ?? 0, Value(text, "WindowHeight") ?? 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, 0, 0); }
    }

    /// <summary>Extra start parameters for the chosen mode (-window for windowed, which DayZ also understands).</summary>
    public static IEnumerable<string> Arguments(LauncherSettings s) =>
        s.DisplayMode == Windowed ? ["-window"] : [];

    /// <summary>
    /// Writes the chosen display mode into DayZ.cfg before launching. Does nothing for "use DayZ's
    /// own setting", or when the file doesn't exist yet (DayZ makes it on its first start).
    /// Never throws; returns what it did, for the log.
    /// </summary>
    public static string Apply(LauncherSettings s, string? path = null)
    {
        if (s.DisplayMode is not (Fullscreen or Windowed)) return "display: DayZ's own setting";
        path ??= ConfigPath;
        try
        {
            if (!File.Exists(path)) return "display: DayZ.cfg not found yet (DayZ makes it on its first start); using -window only";
            var original = File.ReadAllText(path);
            var text = original;
            text = Set(text, "Windowed", s.DisplayMode == Windowed ? 1 : 0);
            if (s.DisplayMode == Windowed && s.WindowWidth >= MinSize && s.WindowHeight >= 360)
            {
                text = Set(text, "WindowWidth", Math.Min(s.WindowWidth, MaxSize));
                text = Set(text, "WindowHeight", Math.Min(s.WindowHeight, MaxSize));
            }
            var what = s.DisplayMode == Windowed ? $"windowed {s.WindowWidth}x{s.WindowHeight}" : "fullscreen";
            if (text == original) return $"display: {what} (DayZ.cfg already set)";
            var backup = BackupPath(path);
            if (!File.Exists(backup)) File.Copy(path, backup);
            var temp = path + ".mmlauncher-tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return $"display: {what} (DayZ.cfg updated)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Couldn't set DayZ's display mode in DayZ.cfg: " + ex.Message);
            return "display: couldn't update DayZ.cfg (" + ex.Message + ")";
        }
    }

    [GeneratedRegex(@"^(?<key>[A-Za-z_0-9]+)\s*=\s*(?<value>-?\d+)\s*;", RegexOptions.Multiline)]
    private static partial Regex LineRx();

    private static int? Value(string text, string key)
    {
        foreach (Match m in LineRx().Matches(text))
            if (m.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(m.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
        return null;
    }

    /// <summary>Replaces "Key=…;" (keeping the file's line endings), or adds it at the end.</summary>
    public static string Set(string text, string key, int value)
    {
        var rx = new Regex($@"^(?<key>{Regex.Escape(key)})\s*=\s*[^;\r\n]*;", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var line = $"{key}={value.ToString(CultureInfo.InvariantCulture)};";
        if (rx.IsMatch(text)) return rx.Replace(text, line, 1);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (text.Length > 0 && !text.EndsWith('\n')) text += newline;
        return text + line + newline;
    }
}
