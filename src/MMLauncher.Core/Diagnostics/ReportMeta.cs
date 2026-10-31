using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLauncher.Core.Diagnostics;

/// <summary>
/// The summary stored as report.json inside every report zip. The agent reads it to write the
/// Discord message, so staff see the important bits without opening the zip.
/// </summary>
public sealed class ReportMeta
{
    public int Format { get; set; } = 1;
    public string LauncherVersion { get; set; } = "";
    public DateTimeOffset Created { get; set; }

    /// <summary>Name the player typed (Discord or in-game name). Optional.</summary>
    public string Player { get; set; } = "";

    /// <summary>What the player says happened. Optional.</summary>
    public string Description { get; set; } = "";

    public string ServerName { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public int ModCount { get; set; }

    /// <summary>When the DayZ session in the report started (from its log file names).</summary>
    public DateTimeOffset? SessionStarted { get; set; }

    /// <summary>One-line crash reason pulled from DayZ's logs, if any ("NULL pointer… in CraftSystem…").</summary>
    public string CrashSummary { get; set; } = "";

    /// <summary>Error codes DayZ showed (e.g. "0x000400F0 kicked by BattlEye").</summary>
    public List<string> ErrorCodes { get; set; } = [];

    public string Windows { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public string Ram { get; set; } = "";

    /// <summary>Files in the zip (names only).</summary>
    public List<string> Files { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>PC details gathered by the launcher (kept separate so Core needs no Windows APIs).</summary>
public sealed record SystemSummary(string Windows, string Cpu, string Gpu, string Ram);
