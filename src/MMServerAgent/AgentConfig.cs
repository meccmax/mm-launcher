using System.Text;
using System.Text.Json;

namespace MMServerAgent;

/// <summary>Optional extras for one server, matched by its game port.</summary>
public sealed class ServerExtras
{
    public string DisplayName { get; set; } = "";
    public string Message { get; set; } = "";
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
    public List<string> ProcessNames { get; set; } = ["DayZServer_x64", "DayZServer"];
    // Problem reports from the launcher
    public bool AcceptReports { get; set; } = true;
    public string DiscordWebhookUrl { get; set; } = "";
    public int MaxReportsPerHourPerAddress { get; set; } = 3;
    public int MaxReportsPerDay { get; set; } = 50;
    public int MaxReportMB { get; set; } = 9;
    public int KeepReportsMB { get; set; } = 500;

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
                    if (!text.Contains("\"AllowPlainHttp\"", StringComparison.OrdinalIgnoreCase))
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
        sb.AppendLine("  // Optional per-server extras, keyed by the server's GAME port (-port=).");
        sb.AppendLine("  //   DisplayName  name shown in the launcher instead of the cfg hostname");
        sb.AppendLine("  //   Message      short note shown to players (events, wipe dates, ...)");
        sb.AppendLine("  // Leave as \"\" to use nothing. Copy the block for more servers.");
        sb.AppendLine("  \"Servers\": {");
        var entries = Servers.ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            var (port, e) = entries[i];
            sb.AppendLine($"    {J(port)}: {{ \"DisplayName\": {J(e.DisplayName)}, \"Message\": {J(e.Message)} }}{(i < entries.Count - 1 ? "," : "")}");
        }
        sb.AppendLine("  }");
        sb.AppendLine("}");
        File.WriteAllText(FilePath, sb.ToString());
    }
}
