using System.Text;
using System.Text.Json;
using MMLauncher.Core.Config;

namespace MMLauncher.Core.Usage;

/// <summary>
/// One thing that happened in the launcher, for MM's anonymous usage stats. Only what's here is
/// ever sent: no names, Steam IDs, file paths or addresses. Servers that aren't MM's are only
/// counted ("Mm": false), never named.
/// </summary>
public sealed class UsageEvent
{
    /// <summary>start | join | play | sync | editor | error</summary>
    public string Type { get; set; } = "";
    public DateTimeOffset At { get; set; }
    /// <summary>join/play: was it an MM server?</summary>
    public bool? Mm { get; set; }
    /// <summary>join/play on an MM server: its name. Empty for anything else.</summary>
    public string Server { get; set; } = "";
    /// <summary>play: seconds DayZ was open; sync: seconds the download took.</summary>
    public int? Seconds { get; set; }
    /// <summary>sync: how many mods were downloaded; join: how many mods the server needs.</summary>
    public int? Count { get; set; }
    /// <summary>sync: bytes downloaded.</summary>
    public long? Bytes { get; set; }
    /// <summary>sync: finished? play: false when DayZ crashed.</summary>
    public bool? Ok { get; set; }
    /// <summary>error: a short fixed code (e.g. "dayz-not-started"), never free text.</summary>
    public string Code { get; set; } = "";

    public static readonly string[] Types = ["start", "join", "play", "sync", "editor", "error"];
}

/// <summary>POST /mm-agent/v1/usage: a batch of events from one launcher.</summary>
public sealed class UsageBatch
{
    public const string Path = "/mm-agent/v1/usage";
    public const int MaxEvents = 200;
    public const int MaxBytes = 128 * 1024;

    /// <summary>The random ID made when the player turned stats on (32 hex characters).</summary>
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>e.g. "Windows 11 (26100)".</summary>
    public string Os { get; set; } = "";
    public List<UsageEvent> Events { get; set; } = [];

    /// <summary>Everything is checked again by the agent: fixed types, short codes, sane numbers.</summary>
    public static UsageBatch? Clean(UsageBatch? b, DateTimeOffset now)
    {
        if (b == null || !Guid.TryParseExact(b.Id ?? "", "N", out _)) return null;
        static string Clip(string? s, int max)
        {
            var t = new string((s ?? "").Where(c => !char.IsControl(c) && c is not ('‪' or '‫' or '‬' or '‭' or '‮')).ToArray()).Trim();
            return t.Length > max ? t[..max] : t;
        }
        static string CodeOnly(string? s) => new string((s ?? "").ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40).ToArray());
        var events = (b.Events ?? []).Take(MaxEvents)
            .Where(e => e != null && UsageEvent.Types.Contains(e.Type) && e.At > now.AddDays(-30) && e.At < now.AddHours(1))
            .Select(e => new UsageEvent
            {
                Type = e.Type,
                At = e.At,
                Mm = e.Mm,
                Server = e.Mm == true ? Clip(e.Server, 120) : "",
                Seconds = e.Seconds is { } s ? Math.Clamp(s, 0, 7 * 24 * 3600) : null,
                Count = e.Count is { } c ? Math.Clamp(c, 0, 10_000) : null,
                Bytes = e.Bytes is { } by ? Math.Clamp(by, 0, 1L << 42) : null,
                Ok = e.Ok,
                Code = CodeOnly(e.Code),
            }).ToList();
        return new UsageBatch { Id = b.Id!.ToLowerInvariant(), Version = Clip(b.Version, 20), Os = Clip(b.Os, 60), Events = events };
    }
}

/// <summary>
/// Events waiting to be sent (usage-queue.jsonc in the launcher's folder), kept when no MM
/// agent is reachable. Only used while stats are turned on; emptied when they're turned off.
/// </summary>
public sealed class UsageQueue
{
    public List<UsageEvent> Events { get; set; } = [];
    private readonly object _lock = new();

    public static string FilePath => System.IO.Path.Combine(LauncherSettings.DataFolder, "usage-queue.jsonc");

    public static UsageQueue Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<UsageQueue>(File.ReadAllText(FilePath), new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true,
                }) is { } q)
                return q;
        }
        catch (Exception) { }
        return new UsageQueue();
    }

    public void Add(UsageEvent e)
    {
        lock (_lock)
        {
            Events.Add(e);
            // Never grows without limit: keep the newest, and nothing older than a week.
            Events.RemoveAll(x => x.At < DateTimeOffset.Now.AddDays(-7));
            if (Events.Count > UsageBatch.MaxEvents) Events.RemoveRange(0, Events.Count - UsageBatch.MaxEvents);
            Save();
        }
    }

    public List<UsageEvent> Snapshot() { lock (_lock) return Events.ToList(); }

    public void Remove(IReadOnlyCollection<UsageEvent> sent)
    {
        lock (_lock) { Events.RemoveAll(sent.Contains); Save(); }
    }

    public void Clear()
    {
        lock (_lock) { Events.Clear(); try { File.Delete(FilePath); } catch { } }
    }

    private void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("// MM Launcher: anonymous usage stats waiting to be sent to an MM server (only while");
            sb.AppendLine("// \"UsageStats\" is \"on\" in settings.jsonc). Removed once sent; emptied when you turn stats off.");
            sb.AppendLine("// Each event: Type (start/join/play/sync/editor/error), At (time), Mm (MM server?), Server (MM");
            sb.AppendLine("// server name only), Seconds, Count, Bytes, Ok, Code (short error code). Safe to delete.");
            sb.Append(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine();
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception) { }
    }
}
