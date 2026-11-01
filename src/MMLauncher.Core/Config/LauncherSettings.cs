using System.Text;
using System.Text.Json;

namespace MMLauncher.Core.Config;

/// <summary>
/// User-editable launcher settings, stored as settings.jsonc in the data folder
/// (%APPDATA%\MMLauncher on Windows). The file is written with comments that explain
/// every option, and comments are allowed when it's read back.
/// </summary>
public sealed class LauncherSettings
{
    public List<string> Hosts { get; set; } = ["game.misfitmercenaries.com", "servers.misfitmercenaries.com"];
    public int ScanPortStart { get; set; } = 2000;
    public int ScanPortEnd { get; set; } = 3000;
    public int AutoRefreshSeconds { get; set; } = 30;
    public int RescanEveryHours { get; set; } = 24;
    public string SteamPathOverride { get; set; } = "";
    public string DayZPathOverride { get; set; } = "";
    public string WorkshopPathOverride { get; set; } = "";
    public List<int> AgentPorts { get; set; } = [2999, 2998, 2997, 2996, 2995, 2994];
    public string LaunchMethod { get; set; } = "Direct";
    public string ModFolderMode { get; set; } = "Junction";
    public string PlayerName { get; set; } = "";
    public string ExtraLaunchParameters { get; set; } = "-nolauncher -world=empty";
    public string ReportName { get; set; } = "";
    public bool CheckForUpdates { get; set; } = true;
    public bool AutoRejoin { get; set; }
    public string Theme { get; set; } = "mm-orange";
    /// <summary>"ask" (not answered yet: nothing is sent), "on" or "off".</summary>
    public string UsageStats { get; set; } = "ask";
    /// <summary>Random ID for anonymous usage stats; made when stats are turned on, cleared when off.</summary>
    public string UsageId { get; set; } = "";
    public int SettingsVersion { get; set; } = CurrentVersion;

    /// <summary>Bump when a default changes in a way existing files should pick up.</summary>
    private const int CurrentVersion = 4;

    public bool LaunchDirect => LaunchMethod.Equals("Direct", StringComparison.OrdinalIgnoreCase);
    public bool UseJunctions => !ModFolderMode.Equals("Absolute", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes "—", "é" etc. as-is instead of \u escapes, so the file stays readable.</summary>
    private static readonly JsonSerializerOptions Pretty = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string DataFolder
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("MMLAUNCHER_DATA");
            var dir = !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MMLauncher");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string FilePath => Path.Combine(DataFolder, "settings.jsonc");

    /// <summary>Loads settings, creating the file with defaults the first time.</summary>
    public static LauncherSettings Load()
    {
        var path = FilePath;
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(text, ReadOptions);
                if (loaded != null)
                {
                    loaded.Normalize();
                    // File from an older version: rewrite it so new options (and their
                    // comments) appear, keeping the user's values.
                    if (!text.Contains("\"SettingsVersion\"", StringComparison.OrdinalIgnoreCase))
                    {
                        // Version 1 files: "Steam" launches could start DayZ without BattlEye
                        // (kicked with 0x000400F0), so move them to the new "Direct" default.
                        if (loaded.LaunchMethod.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                            loaded.LaunchMethod = "Direct";
                        loaded.SettingsVersion = 1;
                    }
                    if (loaded.SettingsVersion < CurrentVersion)
                    {
                        loaded.SettingsVersion = CurrentVersion;
                        loaded.Save();
                    }
                    return loaded;
                }
            }
            catch (JsonException)
            {
                // Broken by a hand edit: keep a copy so nothing is lost, then start fresh.
                File.Copy(path, path + ".broken", overwrite: true);
            }
        }
        var defaults = new LauncherSettings();
        defaults.Save();
        return defaults;
    }

    private void Normalize()
    {
        ScanPortStart = Math.Clamp(ScanPortStart, 1, 65535);
        ScanPortEnd = Math.Clamp(ScanPortEnd, ScanPortStart, 65535);
        if (AutoRefreshSeconds is > 0 and < 10) AutoRefreshSeconds = 10;
        Hosts = Hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LaunchMethod = string.IsNullOrWhiteSpace(LaunchMethod) ? "Steam" : LaunchMethod.Trim();
        ModFolderMode = string.IsNullOrWhiteSpace(ModFolderMode) ? "Junction" : ModFolderMode.Trim();
        PlayerName = (PlayerName ?? "").Trim();
        SteamPathOverride = (SteamPathOverride ?? "").Trim();
        DayZPathOverride = (DayZPathOverride ?? "").Trim();
        WorkshopPathOverride = (WorkshopPathOverride ?? "").Trim();
        ExtraLaunchParameters ??= "";
        AgentPorts = (AgentPorts ?? []).Where(p => p is > 0 and <= 65535).Distinct().ToList();
        Theme = string.IsNullOrWhiteSpace(Theme) ? "mm-orange" : Theme.Trim();
        UsageStats = (UsageStats ?? "").Trim().ToLowerInvariant() is "on" or "off" ? UsageStats!.Trim().ToLowerInvariant() : "ask";
        if (UsageStats == "on" && !Guid.TryParse(UsageId, out _)) UsageId = Guid.NewGuid().ToString("N");
        if (UsageStats != "on") UsageId = "";
    }

    public bool SendsUsageStats => UsageStats == "on" && UsageId.Length > 0;

    /// <summary>Turns anonymous usage stats on (new random ID if there's none) or off (ID forgotten).</summary>
    public void SetUsageStats(bool on)
    {
        UsageStats = on ? "on" : "off";
        UsageId = on ? (Guid.TryParse(UsageId, out _) ? UsageId : Guid.NewGuid().ToString("N")) : "";
    }

    public void Save()
    {
        static string J<T>(T v) => JsonSerializer.Serialize(v, Pretty);
        var sb = new StringBuilder();
        sb.AppendLine("// ===================================================================");
        sb.AppendLine("// MM Launcher settings");
        sb.AppendLine("// Purpose: tells the launcher where to look for Misfit Mercenaries");
        sb.AppendLine("// servers and how often to update them. Safe to edit by hand while the");
        sb.AppendLine("// launcher is closed. Delete this file to go back to the defaults.");
        sb.AppendLine("// ===================================================================");
        sb.AppendLine("{");
        sb.AppendLine("  // Hostnames (or IPs) that MM servers run on. Every one is scanned.");
        sb.AppendLine("  // If two names point at the same machine it is only scanned once.");
        sb.AppendLine($"  \"Hosts\": {J(Hosts)},");
        sb.AppendLine();
        sb.AppendLine("  // Port range to scan on each host. Any port in this range that answers");
        sb.AppendLine("  // a Steam server query is treated as a server's query port.");
        sb.AppendLine($"  \"ScanPortStart\": {ScanPortStart},");
        sb.AppendLine($"  \"ScanPortEnd\": {ScanPortEnd},");
        sb.AppendLine();
        sb.AppendLine("  // Seconds between automatic status refreshes (player counts, ping, mods).");
        sb.AppendLine("  // 0 = only refresh when you click Refresh. Minimum 10.");
        sb.AppendLine($"  \"AutoRefreshSeconds\": {AutoRefreshSeconds},");
        sb.AppendLine();
        sb.AppendLine("  // Re-run the full port scan on startup if the last one is older than this");
        sb.AppendLine("  // many hours (picks up new servers). 0 = only scan when you click Rescan.");
        sb.AppendLine($"  \"RescanEveryHours\": {RescanEveryHours},");
        sb.AppendLine();
        sb.AppendLine("  // ---------------- Folders (all can be set in the launcher's Settings) ----------------");
        sb.AppendLine("  // Leave any of these empty (\"\") to find it automatically: Steam from the registry,");
        sb.AppendLine("  // DayZ from Steam's library list, mods from the library DayZ is installed in.");
        sb.AppendLine("  // Steam install folder, e.g. \"C:\\\\Program Files (x86)\\\\Steam\".");
        sb.AppendLine($"  \"SteamPathOverride\": {J(SteamPathOverride)},");
        sb.AppendLine("  // DayZ game folder (the one with DayZ_BE.exe), e.g. \"E:\\\\SteamLibrary\\\\steamapps\\\\common\\\\DayZ\".");
        sb.AppendLine($"  \"DayZPathOverride\": {J(DayZPathOverride)},");
        sb.AppendLine("  // DayZ workshop mods folder (holds the numbered mod folders), e.g.");
        sb.AppendLine("  // \"E:\\\\SteamLibrary\\\\steamapps\\\\workshop\\\\content\\\\221100\". Steam always downloads");
        sb.AppendLine("  // into its own library, so only change this if mods really live elsewhere.");
        sb.AppendLine($"  \"WorkshopPathOverride\": {J(WorkshopPathOverride)},");
        sb.AppendLine();
        sb.AppendLine("  // TCP ports where MM Server Agents may be listening on each host. An agent");
        sb.AppendLine("  // reports a server's true, complete mod list and full name. Servers without");
        sb.AppendLine("  // an agent still work; their mods come from the server's status reply.");
        sb.AppendLine("  // [] = don't look for agents.");
        sb.AppendLine($"  \"AgentPorts\": {J(AgentPorts)},");
        sb.AppendLine();
        sb.AppendLine("  // ---------------- Joining a server ----------------");
        sb.AppendLine("  // How DayZ is started when you click JOIN SERVER:");
        sb.AppendLine("  //   \"Direct\" run DayZ_BE.exe (BattlEye's launcher) from the DayZ folder, which");
        sb.AppendLine("  //            starts BattlEye and then the game (default, recommended)");
        sb.AppendLine("  //   \"Steam\"  ask Steam to start DayZ (steam.exe -applaunch). May start DayZ");
        sb.AppendLine("  //            without BattlEye, which gets you kicked on BattlEye servers");
        sb.AppendLine($"  \"LaunchMethod\": {J(LaunchMethod)},");
        sb.AppendLine();
        sb.AppendLine("  // How mods are handed to DayZ:");
        sb.AppendLine("  //   \"Junction\" short links in DayZ\\!MMWorkshop pointing at Steam's workshop");
        sb.AppendLine("  //              folders (no copies, no spaces in paths) (default)");
        sb.AppendLine("  //   \"Absolute\" full paths to Steam's workshop folders");
        sb.AppendLine($"  \"ModFolderMode\": {J(ModFolderMode)},");
        sb.AppendLine();
        sb.AppendLine("  // In-game profile name passed as -name=. Empty = DayZ's own profile name.");
        sb.AppendLine($"  \"PlayerName\": {J(PlayerName)},");
        sb.AppendLine();
        sb.AppendLine("  // Extra DayZ start parameters added to every launch.");
        sb.AppendLine("  //   -nolauncher   skip the official DayZ launcher window");
        sb.AppendLine("  //   -world=empty  load faster by not loading a menu world first");
        sb.AppendLine($"  \"ExtraLaunchParameters\": {J(ExtraLaunchParameters)},");
        sb.AppendLine();
        sb.AppendLine("  // Name put on problem reports so staff know who sent them (Discord or in-game name).");
        sb.AppendLine("  // Remembered from the last report you sent.");
        sb.AppendLine($"  \"ReportName\": {J(ReportName)},");
        sb.AppendLine();
        sb.AppendLine("  // Check GitHub for a newer launcher on startup and every few hours, and offer a");
        sb.AppendLine("  // one-click update. Nothing is installed without you clicking Update. false = never check.");
        sb.AppendLine($"  \"CheckForUpdates\": {J(CheckForUpdates)},");
        sb.AppendLine();
        sb.AppendLine("  // Rejoin automatically when DayZ CRASHES after the launcher started it (a normal quit");
        sb.AppendLine("  // never rejoins). Waits for the server if it's restarting, updates mods first, and");
        sb.AppendLine("  // shows a countdown you can cancel. Same as the checkbox under JOIN SERVER.");
        sb.AppendLine($"  \"AutoRejoin\": {J(AutoRejoin)},");
        sb.AppendLine();
        sb.AppendLine("  // Color scheme (also in Settings): \"mm-orange\" (default), \"arctic-blue\", \"toxic-green\",");
        sb.AppendLine("  // \"blood-red\", \"royal-purple\", \"steel\" or \"daylight\" (light).");
        sb.AppendLine($"  \"Theme\": {J(Theme)},");
        sb.AppendLine();
        sb.AppendLine("  // Anonymous usage stats sent to MM's own servers to help improve the launcher (also in Settings):");
        sb.AppendLine("  //   \"ask\" ask once on the next start (nothing is sent until you answer)");
        sb.AppendLine("  //   \"on\"  send: launcher version, Windows version, joins and play time on MM servers,");
        sb.AppendLine("  //         joins to other servers (counted only, never which), mod download times, errors");
        sb.AppendLine("  //   \"off\" send nothing");
        sb.AppendLine("  // No names, Steam IDs or IP addresses are kept. UsageId is a random number made when you turn");
        sb.AppendLine("  // this on (empty when off), so the stats can tell installs apart without knowing whose they are.");
        sb.AppendLine($"  \"UsageStats\": {J(UsageStats)},");
        sb.AppendLine($"  \"UsageId\": {J(UsageId)},");
        sb.AppendLine();
        sb.AppendLine("  // Format version of this file. Managed by the launcher; don't change it.");
        sb.AppendLine($"  \"SettingsVersion\": {SettingsVersion}");
        sb.AppendLine("}");
        File.WriteAllText(FilePath, sb.ToString());
    }
}
