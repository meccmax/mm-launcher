using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLauncher.Core.Agent;

/// <summary>
/// What the MM Server Agent sends to the launcher. Shared by both programs so they
/// can never disagree about the format. Served at GET /mm-agent/v1/servers.
/// </summary>
public sealed class AgentResponse
{
    public string Agent { get; set; } = "MM Server Agent";
    public string Version { get; set; } = "";
    public DateTimeOffset Generated { get; set; }
    public List<AgentServerInfo> Servers { get; set; } = [];

    /// <summary>True when this agent accepts problem reports (POST /mm-agent/v1/report).</summary>
    public bool ReportsEnabled { get; set; }

    /// <summary>True when this agent serves the list of all DayZ servers (GET /mm-agent/v1/browser).</summary>
    public bool BrowserFeed { get; set; }

    /// <summary>How many shared DayZ Editor presets this agent offers (GET /mm-agent/v1/editor-presets).</summary>
    public int EditorPresets { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>What the agent answers after a report upload.</summary>
public sealed class ReportUploadResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";

    /// <summary>Short reference staff can search for (also in the Discord post and file name).</summary>
    public string Reference { get; set; } = "";
}

/// <summary>One DayZ server running on the agent's machine.</summary>
public sealed class AgentServerInfo
{
    /// <summary>Full hostname from serverDZ.cfg (no 63-character cutoff).</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional friendlier name set in agent.jsonc.</summary>
    public string? DisplayName { get; set; }

    public int GamePort { get; set; }
    public int QueryPort { get; set; }
    public int MaxPlayers { get; set; }

    /// <summary>Map from the mission template, e.g. "deerisle".</summary>
    public string Map { get; set; } = "";

    /// <summary>How long the server process has been running.</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>Optional message for players, set in agent.jsonc.</summary>
    public string? Message { get; set; }

    /// <summary>When the agent expects the next restart (from agent.jsonc's restart schedule), if known.</summary>
    public DateTimeOffset? NextRestart { get; set; }

    /// <summary>True when NextRestart is an estimate (interval since start) rather than a fixed time.</summary>
    public bool RestartApproximate { get; set; }

    /// <summary>True when the agent has player-count history for this server (GET /mm-agent/v1/history?port=).</summary>
    public bool HasHistory { get; set; }

    /// <summary>News posts from the agent's news folder (null = none). Newest first.</summary>
    public List<News.NewsItem>? News { get; set; }

    /// <summary>Client mods in -mod= order. This is the true list the server loads.</summary>
    public List<AgentMod> Mods { get; set; } = [];
}

public sealed class AgentMod
{
    /// <summary>Steam Workshop ID, or 0 for a local mod with no workshop page.</summary>
    public ulong Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Folder name only (e.g. "@CF"), never a full path.</summary>
    public string Folder { get; set; } = "";
}
