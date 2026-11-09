using System.Text;
using System.Text.Json;

namespace MMServerAgent;

/// <summary>Optional extras for one server, matched by its game port.</summary>
public sealed class ServerExtras
{
    public string DisplayName { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>Overrides the global restart schedule for this server (null = use the global one).</summary>
    public double? RestartEveryHours { get; set; }
    public List<string>? RestartTimes { get; set; }
}

/// <summary>
/// Agent settings, stored as agent.jsonc next to MMServerAgent.exe.
/// Written with comments that explain every option; comments are allowed when read back.
/// </summary>
public sealed class AgentConfig
{
    public int Port { get; set; } = 2999;
    public int RescanSeconds { get; set; } = 30;
    public bool AllowPlainHttp { get; set; } = true;
    public string SteamWebApiKey { get; set; } = "";
    public int ServerListRefreshMinutes { get; set; } = 5;
    public bool CountryLookup { get; set; } = true;
    public bool ModSearch { get; set; } = true;
    public int ModListRefreshMinutes { get; set; } = 30;
    public double RestartEveryHours { get; set; }
    public List<string> RestartTimes { get; set; } = [];
    public List<string> ProcessNames { get; set; } = ["DayZServer_x64", "DayZServer"];
    // Problem reports from the launcher
    public bool AcceptReports { get; set; } = true;
    public string DiscordWebhookUrl { get; set; } = "";
    public int MaxReportsPerHourPerAddress { get; set; } = 3;
    public int MaxReportsPerDay { get; set; } = 50;
    public int MaxReportMB { get; set; } = 9;
    public int KeepReportsMB { get; set; } = 500;
    // Anonymous launcher usage stats
    public bool AcceptUsageStats { get; set; } = true;
    public int KeepUsageDays { get; set; } = 90;
    // MM BlackBox stats for the website leaderboards
    public string BlackBoxStatsToken { get; set; } = "";

    public Dictionary<string, ServerExtras> Servers { get; set; } = new()
    {
        ["2302"] = new ServerExtras { DisplayName = "", Message = "" },
    };

    /// <summary>Writes "—", "é" etc. as-is instead of \u escapes, so the file stays readable.</summary>
    private static readonly JsonSerializerOptions Pretty = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "agent.jsonc");

    public static AgentConfig Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var text = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AgentConfig>(text, ReadOptions);
                if (cfg != null)
                {
                    cfg.Port = Math.Clamp(cfg.Port, 1, 65535);
                    cfg.RescanSeconds = Math.Max(5, cfg.RescanSeconds);
                    cfg.MaxReportMB = Math.Clamp(cfg.MaxReportMB, 1, 50);
                    cfg.MaxReportsPerHourPerAddress = Math.Max(1, cfg.MaxReportsPerHourPerAddress);
                    cfg.MaxReportsPerDay = Math.Max(1, cfg.MaxReportsPerDay);
                    cfg.KeepReportsMB = Math.Max(10, cfg.KeepReportsMB);
                    cfg.DiscordWebhookUrl = (cfg.DiscordWebhookUrl ?? "").Trim();
                    // File from an older agent: rewrite it so the new options (with their
                    // comments) appear, keeping every value that was already set.
                    // (Checks for the newest option; older files lack it too.)
                    cfg.SteamWebApiKey = (cfg.SteamWebApiKey ?? "").Trim();
                    cfg.RestartEveryHours = Math.Clamp(cfg.RestartEveryHours, 0, 168);
                    cfg.RestartTimes ??= [];
                    cfg.KeepUsageDays = Math.Clamp(cfg.KeepUsageDays, 7, 3650);
                    cfg.ModListRefreshMinutes = Math.Clamp(cfg.ModListRefreshMinutes, 10, 1440);
                    cfg.BlackBoxStatsToken = (cfg.BlackBoxStatsToken ?? "").Trim();
                    if (!text.Contains("\"BlackBoxStatsToken\"", StringComparison.OrdinalIgnoreCase))
                        cfg.Save();
                    return cfg;
                }
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"agent.jsonc couldn't be read ({ex.Message}). Using defaults; your file was left untouched.");
                return new AgentConfig();
            }
        }
        var defaults = new AgentConfig();
        defaults.Save();
        return defaults;
    }

    public ServerExtras? ExtrasFor(int gamePort) =>
        Servers.TryGetValue(gamePort.ToString(), out var e) ? e : null;

    public void Save()
    {
        static string J<T>(T v) => JsonSerializer.Serialize(v, Pretty);
        var sb = new StringBuilder();
        sb.AppendLine("// ===================================================================");
        sb.AppendLine("// MM Server Agent settings");
        sb.AppendLine("// Purpose: the agent watches the DayZ servers running on THIS machine");
        sb.AppendLine("// and tells the MM Launcher their true mod lists and full names.");
        sb.AppendLine("// It only reads; it never changes server files.");
        sb.AppendLine("// After editing, restart the agent (or the \"MM Server Agent\" service).");
        sb.AppendLine("// ===================================================================");
        sb.AppendLine("{");
        sb.AppendLine("  // TCP port the launcher connects to. Give each server machine its own");
        sb.AppendLine("  // port (2999, 2998, 2997, ...) when they share one public IP, and");
        sb.AppendLine("  // forward it on the router. \"MMServerAgent.exe install\" opens it in");
        sb.AppendLine("  // Windows Firewall. The launcher checks 2994-2999 by default.");
        sb.AppendLine($"  \"Port\": {Port},");
        sb.AppendLine();
        sb.AppendLine("  // How often (seconds) to re-check which servers are running and with");
        sb.AppendLine("  // which mods. A restart with new mods shows up within this time.");
        sb.AppendLine($"  \"RescanSeconds\": {RescanSeconds},");
        sb.AppendLine();
        sb.AppendLine("  // Launchers from v0.5 on talk to the agent encrypted (HTTPS, with the key in");
        sb.AppendLine("  // agent-tls.pfx). true = also answer older launchers unencrypted while players update.");
        sb.AppendLine("  // false = encrypted only (recommended once everyone is on v0.5 or newer).");
        sb.AppendLine($"  \"AllowPlainHttp\": {J(AllowPlainHttp)},");
        sb.AppendLine();
        sb.AppendLine("  // ---------------- \"All servers\" browser in the launcher ----------------");
        sb.AppendLine("  // Steam Web API key (free: steamcommunity.com/dev/apikey; the Steam account needs");
        sb.AppendLine("  // $5 spent). With it, this agent fetches Valve's list of every DayZ server and passes");
        sb.AppendLine("  // it to launchers for their server browser. The key stays here; launchers never see it.");
        sb.AppendLine("  // Keep it secret. \"\" = browser feed off (the MM server list still works).");
        sb.AppendLine($"  \"SteamWebApiKey\": {J(SteamWebApiKey)},");
        sb.AppendLine();
        sb.AppendLine("  // How often (minutes, 2 or more) to fetch a fresh list from Steam.");
        sb.AppendLine($"  \"ServerListRefreshMinutes\": {ServerListRefreshMinutes},");
        sb.AppendLine();
        sb.AppendLine("  // true = show which country each server is in (and let players filter by country or");
        sb.AppendLine("  // continent). Uses DB-IP's free country database (db-ip.com, CC BY 4.0), downloaded");
        sb.AppendLine("  // once a month into the \"geoip\" folder (about 5 MB). Lookups happen on this machine;");
        sb.AppendLine("  // no address is sent anywhere. false = no countries.");
        sb.AppendLine($"  \"CountryLookup\": {J(CountryLookup)},");
        sb.AppendLine();
        sb.AppendLine("  // true = also collect each modded server's mod list, so players can search the browser by");
        sb.AppendLine("  // mod (\"Terje\", \"Expansion\") and filter for servers with AI. The agent asks every modded");
        sb.AppendLine("  // server for its mod list (the same standard query any server browser sends), a few dozen");
        sb.AppendLine("  // at a time. false = search by server name only.");
        sb.AppendLine($"  \"ModSearch\": {J(ModSearch)},");
        sb.AppendLine("  // How often (minutes, 10-1440) to collect the mod lists again. Each round takes a few minutes.");
        sb.AppendLine($"  \"ModListRefreshMinutes\": {ModListRefreshMinutes},");
        sb.AppendLine();
        sb.AppendLine("  // ---------------- Restart schedule (shown in the launcher as \"Next restart\") ----------------");
        sb.AppendLine("  // Use ONE of these. Each server can override them in \"Servers\" below.");
        sb.AppendLine("  //   RestartEveryHours  restarts every N hours counted from when the server started");
        sb.AppendLine("  //                      (how CFTools / BattlEye scheduled restarts work), e.g. 4");
        sb.AppendLine("  //   RestartTimes       fixed clock times in this machine's time zone, e.g. [\"00:00\", \"06:00\"]");
        sb.AppendLine("  // 0 and [] = no schedule (the launcher shows no restart times).");
        sb.AppendLine($"  \"RestartEveryHours\": {J(RestartEveryHours)},");
        sb.AppendLine($"  \"RestartTimes\": {J(RestartTimes)},");
        sb.AppendLine();
        sb.AppendLine("  // Process names that count as a DayZ server (without .exe).");
        sb.AppendLine($"  \"ProcessNames\": {J(ProcessNames)},");
        sb.AppendLine();
        sb.AppendLine("  // ---------------- Problem reports (\"Report a problem\" in the launcher) ----------------");
        sb.AppendLine("  // true = players can send diagnostics (DayZ logs, crash info, PC basics) to this agent.");
        sb.AppendLine("  // Every report is saved in the \"reports\" folder next to this exe, and posted to");
        sb.AppendLine("  // Discord if a webhook is set below. false = the launcher won't offer \"Send to staff\".");
        sb.AppendLine($"  \"AcceptReports\": {J(AcceptReports)},");
        sb.AppendLine();
        sb.AppendLine("  // Discord webhook for the staff channel reports go to. Discord: channel settings >");
        sb.AppendLine("  // Integrations > Webhooks > New Webhook > Copy Webhook URL. Keep it secret; it only");
        sb.AppendLine("  // lives here on the server, never in the launcher. \"\" = save reports on disk only.");
        sb.AppendLine($"  \"DiscordWebhookUrl\": {J(DiscordWebhookUrl)},");
        sb.AppendLine();
        sb.AppendLine("  // Spam limits. Reports from one internet address per hour, and from everyone per day.");
        sb.AppendLine($"  \"MaxReportsPerHourPerAddress\": {MaxReportsPerHourPerAddress},");
        sb.AppendLine($"  \"MaxReportsPerDay\": {MaxReportsPerDay},");
        sb.AppendLine();
        sb.AppendLine("  // Largest report accepted, in MB. Discord webhooks can't take files over 10 MB.");
        sb.AppendLine($"  \"MaxReportMB\": {MaxReportMB},");
        sb.AppendLine();
        sb.AppendLine("  // Disk space the saved reports may use, in MB. Oldest are deleted first.");
        sb.AppendLine($"  \"KeepReportsMB\": {KeepReportsMB},");
        sb.AppendLine();
        sb.AppendLine("  // ---------------- Anonymous launcher usage stats ----------------");
        sb.AppendLine("  // true = launchers whose players said yes send anonymous stats here: launcher and Windows");
        sb.AppendLine("  // version, joins and play time on MM servers, joins to other servers (counted only), mod");
        sb.AppendLine("  // download times, errors and crashes. Saved as one file per day in the \"usage\" folder");
        sb.AppendLine("  // next to this exe, with a readable summary in usage\\summary.txt. No names, Steam IDs or");
        sb.AppendLine("  // IP addresses are stored. false = launchers don't send any.");
        sb.AppendLine($"  \"AcceptUsageStats\": {J(AcceptUsageStats)},");
        sb.AppendLine("  // Days of usage stats to keep (7-3650). Older day files are deleted.");
        sb.AppendLine($"  \"KeepUsageDays\": {KeepUsageDays},");
        sb.AppendLine();
        sb.AppendLine("  // ---------------- MM BlackBox stats for the website ----------------");
        sb.AppendLine("  // Password the MM website's stats job uses to download each DayZ server's MM BlackBox");
        sb.AppendLine("  // stats file (<profiles>\\MM_BlackBox\\Stats\\public_stats.json) for the leaderboards on");
        sb.AppendLine("  // misfitmercenaries.com. Only those stats files can be fetched, only over an encrypted");
        sb.AppendLine("  // connection, and only with this password. \"\" = off.");
        sb.AppendLine("  // Make one by running stats-token.bat as administrator. Keep it secret: the same password");
        sb.AppendLine("  // goes in the website repo's BLACKBOX_TOKEN secret (GitHub > Settings > Secrets > Actions).");
        sb.AppendLine($"  \"BlackBoxStatsToken\": {J(BlackBoxStatsToken)},");
        sb.AppendLine();
        sb.AppendLine("  // Optional per-server extras, keyed by the server's GAME port (-port=).");
        sb.AppendLine("  //   DisplayName  name shown in the launcher instead of the cfg hostname");
        sb.AppendLine("  //   Message      short note shown to players (events, wipe dates, ...)");
        sb.AppendLine("  //   RestartEveryHours / RestartTimes  (optional) this server's own restart schedule");
        sb.AppendLine("  // Leave as \"\" to use nothing. Copy the block for more servers.");
        sb.AppendLine("  \"Servers\": {");
        var entries = Servers.ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            var (port, e) = entries[i];
            var schedule = (e.RestartEveryHours is { } h ? $", \"RestartEveryHours\": {J(h)}" : "") +
                           (e.RestartTimes is { } t ? $", \"RestartTimes\": {J(t)}" : "");
            sb.AppendLine($"    {J(port)}: {{ \"DisplayName\": {J(e.DisplayName)}, \"Message\": {J(e.Message)}{schedule} }}{(i < entries.Count - 1 ? "," : "")}");
        }
        sb.AppendLine("  }");
        sb.AppendLine("}");
        File.WriteAllText(FilePath, sb.ToString());
    }
}
