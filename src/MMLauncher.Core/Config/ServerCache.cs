using System.Text;
using System.Text.Json;

namespace MMLauncher.Core.Config;

/// <summary>One server remembered from the last scan.</summary>
public sealed class CachedServer
{
    public string Host { get; set; } = "";
    public int QueryPort { get; set; }
    public int? GamePort { get; set; }
    public string LastKnownName { get; set; } = "";
}

/// <summary>
/// The list of servers found by the last scan, stored as servers.jsonc next to the
/// settings. Lets the launcher show servers instantly on startup instead of scanning
/// 2,000 ports every time.
/// </summary>
public sealed class ServerCache
{
    public DateTimeOffset? LastScan { get; set; }
    public List<CachedServer> Servers { get; set; } = [];

    public static string FilePath => Path.Combine(LauncherSettings.DataFolder, "servers.jsonc");

    public static ServerCache Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ServerCache>(File.ReadAllText(FilePath), LauncherSettings.ReadOptions) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public void Save()
    {
        var body = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var sb = new StringBuilder();
        sb.AppendLine("// ===================================================================");
        sb.AppendLine("// MM Launcher server cache (written automatically, no need to edit)");
        sb.AppendLine("// Purpose: remembers which servers the last scan found so the launcher");
        sb.AppendLine("// can show them immediately. Deleting it just forces a fresh scan.");
        sb.AppendLine("//   LastScan       when the last full port scan ran");
        sb.AppendLine("//   Host           hostname the server answered on");
        sb.AppendLine("//   QueryPort      Steam query port (where status is asked)");
        sb.AppendLine("//   GamePort       port players connect to (reported by the server)");
        sb.AppendLine("//   LastKnownName  server name at scan time, shown while offline");
        sb.AppendLine("// ===================================================================");
        sb.Append(body);
        File.WriteAllText(FilePath, sb.ToString());
    }
}
