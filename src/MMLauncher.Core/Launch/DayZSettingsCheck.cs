using MMLauncher.Core.Logging;

namespace MMLauncher.Core.Launch;

/// <summary>
/// DayZ's own text settings files (Documents\DayZ\DayZ.cfg and &lt;name&gt;_settings.DayZProfile) can be
/// left full of junk by a crash, a power cut or a cloud-sync clash while DayZ was saving. DayZ then
/// stops at start with "File …DayZProfile, line 0: … encountered instead of '='". Before launching,
/// the launcher looks for that and offers to set the damaged file aside (renamed, not deleted), so
/// DayZ makes a fresh one with default video and audio settings. Key bindings live in other files
/// and aren't touched.
/// </summary>
public static class DayZSettingsCheck
{
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Documents\DayZ (next to DayZ.cfg; follows a Documents folder moved into OneDrive).</summary>
    public static string Folder => Path.GetDirectoryName(GameDisplay.ConfigPath) ?? "";

    /// <summary>The damaged settings files in DayZ's folder (empty if none, or the folder doesn't exist).</summary>
    public static List<string> FindDamaged(string? folder = null)
    {
        folder ??= Folder;
        var damaged = new List<string>();
        try
        {
            if (!Directory.Exists(folder)) return damaged;
            var files = Directory.EnumerateFiles(folder, "*_settings.DayZProfile")
                                 .Append(Path.Combine(folder, "DayZ.cfg"))
                                 .Where(File.Exists);
            foreach (var f in files)
                if (LooksDamaged(f)) damaged.Add(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return damaged;
    }

    /// <summary>
    /// True when a settings file isn't the plain "key=value;" text DayZ writes: control characters
    /// (such as the zero bytes a cut-off write leaves), or no setting line at all.
    /// </summary>
    public static bool LooksDamaged(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0) return false;     // DayZ treats an empty file as defaults
            if (info.Length > MaxBytes) return true;
            var bytes = File.ReadAllBytes(path);
            foreach (var b in bytes)
                if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D)) return true;
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("//"));
            return first != null && !(first.Contains('=') || first.StartsWith("class ", StringComparison.Ordinal) || first == "{" || first == "};");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Renames a damaged file to "….damaged-yyyyMMdd-HHmmss" so DayZ makes a fresh one. Null if it worked, else why not.</summary>
    public static string? SetAside(string path)
    {
        try
        {
            var target = $"{path}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, target);
            Log.Info($"DayZ settings file looked damaged; renamed {Path.GetFileName(path)} to {Path.GetFileName(target)}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
