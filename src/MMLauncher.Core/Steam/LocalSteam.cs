using Microsoft.Win32;

namespace MMLauncher.Core.Steam;

/// <summary>A workshop mod as it sits on this PC.</summary>
public sealed record InstalledMod(ulong Id, string Folder, DateTimeOffset? Updated, long SizeBytes);

/// <summary>
/// Read-only snapshot of this PC's Steam + DayZ install: where things are and which
/// DayZ workshop mods are downloaded. Nothing here changes any files.
/// </summary>
public sealed class LocalSteam
{
    public const string DayZAppId = "221100";

    public string SteamPath { get; private init; } = "";
    public IReadOnlyList<string> Libraries { get; private init; } = [];
    public string? DayZPath { get; private init; }
    public string? DayZLibrary { get; private init; }

    /// <summary>The folder holding the numbered mod folders (…\workshop\content\221100).</summary>
    public string? WorkshopPath { get; private init; }

    public bool SteamIsManual { get; private init; }
    public bool DayZIsManual { get; private init; }
    public bool WorkshopIsManual { get; private init; }
    public IReadOnlyDictionary<ulong, InstalledMod> Mods { get; private init; } = new Dictionary<ulong, InstalledMod>();

    /// <summary>
    /// Finds Steam (registry first, then the default folder), reads its library list, and
    /// locates DayZ and its workshop folder. Any of the three can be set manually instead;
    /// a manual folder always wins. Returns null when neither Steam nor a manual DayZ folder
    /// can be found.
    /// </summary>
    public static LocalSteam? Detect(string? steamOverride = null, string? dayzOverride = null, string? workshopOverride = null)
    {
        static string? Clean(string? p) => string.IsNullOrWhiteSpace(p) ? null : Path.GetFullPath(p.Trim().Trim('"'));
        steamOverride = Clean(steamOverride);
        dayzOverride = Clean(dayzOverride);
        workshopOverride = Clean(workshopOverride);

        var steam = steamOverride ?? FindSteamPath();
        if (steam != null && !Directory.Exists(steam)) steam = null;
        if (steam == null && dayzOverride == null) return null;
        if (steam != null) steam = Path.GetFullPath(steam);

        var libraries = new List<string>();
        if (steam != null)
        {
            libraries.Add(steam);
            var vdfPath = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                try
                {
                    var root = VdfNode.Parse(File.ReadAllText(vdfPath));
                    var folders = root["libraryfolders"] ?? root;
                    foreach (var lib in folders.Children.Values)
                    {
                        var p = lib["path"]?.Value ?? lib.Value;
                        if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p) &&
                            !libraries.Any(l => string.Equals(Path.GetFullPath(l), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)))
                            libraries.Add(p);
                    }
                }
                catch { /* unreadable file: fall back to the main Steam folder only */ }
            }
        }

        // DayZ: a manual folder wins; otherwise the library whose manifest says DayZ is there.
        string? dayzLibrary = null, dayzPath = null;
        if (dayzOverride != null)
        {
            dayzPath = dayzOverride;
            // ...\<library>\steamapps\common\DayZ  ->  <library>
            var common = Directory.GetParent(dayzOverride);
            if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true &&
                common.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
                dayzLibrary = common.Parent.Parent?.FullName;
        }
        else
        {
            foreach (var lib in libraries)
            {
                var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{DayZAppId}.acf");
                if (!File.Exists(manifest)) continue;
                string installDir = "DayZ";
                try { installDir = VdfNode.Parse(File.ReadAllText(manifest))["AppState"]?["installdir"]?.Value ?? "DayZ"; }
                catch { }
                dayzLibrary = lib;
                dayzPath = Path.Combine(lib, "steamapps", "common", installDir);
                break;
            }
        }

        // Workshop: manual folder, else the DayZ library's, else the first library that has one.
        string? workshop = workshopOverride;
        if (workshop == null)
        {
            var candidates = dayzLibrary != null ? [dayzLibrary] : libraries;
            workshop = candidates.Select(ContentDirOf).FirstOrDefault(Directory.Exists)
                       ?? (dayzLibrary != null ? ContentDirOf(dayzLibrary) : null);
        }

        var mods = new Dictionary<ulong, InstalledMod>();
        if (workshop != null) ReadWorkshop(workshop, mods);

        return new LocalSteam
        {
            SteamPath = steam ?? "",
            Libraries = libraries,
            DayZLibrary = dayzLibrary,
            DayZPath = dayzPath != null && Directory.Exists(dayzPath) ? dayzPath : null,
            WorkshopPath = workshop,
            SteamIsManual = steamOverride != null,
            DayZIsManual = dayzOverride != null,
            WorkshopIsManual = workshopOverride != null,
            Mods = mods,
        };
    }

    /// <summary>
    /// When Steam last updated one mod on this PC (appworkshop_221100.acf "timeupdated", the Workshop
    /// version it has), from the mods folder ...\workshop\content\221100. Null if unknown.
    /// </summary>
    public static DateTimeOffset? InstalledVersion(string contentDir, ulong id)
    {
        try
        {
            var workshopDir = Directory.GetParent(contentDir)?.Parent?.FullName;
            if (workshopDir == null) return null;
            var acf = Path.Combine(workshopDir, $"appworkshop_{DayZAppId}.acf");
            if (!File.Exists(acf)) return null;
            var node = VdfNode.Parse(File.ReadAllText(acf))["AppWorkshop"]?["WorkshopItemsInstalled"]?[id.ToString()];
            return long.TryParse(node?["timeupdated"]?.Value, out var t) && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t) : null;
        }
        catch { return null; }
    }

    /// <summary>True when the folder (or any subfolder) contains at least one file.</summary>
    public static bool HasAnyFile(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    public static string ContentDirOf(string library) => Path.Combine(library, "steamapps", "workshop", "content", DayZAppId);

    /// <summary>True when the folder looks like a DayZ install (has the game or BattlEye exe).</summary>
    public static bool LooksLikeDayZ(string? folder) =>
        folder != null && (File.Exists(Path.Combine(folder, "DayZ_BE.exe")) || File.Exists(Path.Combine(folder, "DayZ_x64.exe")));

    private static void ReadWorkshop(string contentDir, Dictionary<ulong, InstalledMod> mods)
    {
        // Steam's bookkeeping file sits two levels up: …\workshop\appworkshop_221100.acf
        var workshopDir = Directory.GetParent(contentDir)?.Parent?.FullName ?? contentDir;
        var acf = Path.Combine(workshopDir, $"appworkshop_{DayZAppId}.acf");

        // appworkshop_221100.acf records when Steam last updated each item locally.
        var stamps = new Dictionary<ulong, (DateTimeOffset? Updated, long Size)>();
        if (File.Exists(acf))
        {
            try
            {
                var installed = VdfNode.Parse(File.ReadAllText(acf))["AppWorkshop"]?["WorkshopItemsInstalled"];
                if (installed != null)
                {
                    foreach (var (key, node) in installed.Children)
                    {
                        if (!ulong.TryParse(key, out var id)) continue;
                        DateTimeOffset? updated = long.TryParse(node["timeupdated"]?.Value, out var t) && t > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(t) : null;
                        long.TryParse(node["size"]?.Value, out var size);
                        stamps[id] = (updated, size);
                    }
                }
            }
            catch { }
        }

        if (!Directory.Exists(contentDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(contentDir))
        {
            if (!ulong.TryParse(Path.GetFileName(dir), out var id)) continue;
            // A folder with no files in it (only empty subfolders) means Steam started but
            // never finished putting the mod there, so it doesn't count as downloaded.
            if (!HasAnyFile(dir)) continue;
            stamps.TryGetValue(id, out var s);
            mods[id] = new InstalledMod(id, dir, s.Updated, s.Size);
        }
    }

    private static string? FindSteamPath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string p && Directory.Exists(p))
                    return p.Replace('/', '\\');
                if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string p2 && Directory.Exists(p2))
                    return p2;
            }
            catch { }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            return Directory.Exists(fallback) ? fallback : null;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { ".steam/steam", ".local/share/Steam" })
        {
            var p = Path.Combine(home, candidate);
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
