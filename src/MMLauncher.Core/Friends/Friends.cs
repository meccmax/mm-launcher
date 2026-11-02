using System.Text;
using System.Text.Json;
using MMLauncher.Core.Config;
using MMLauncher.Core.Steam;

namespace MMLauncher.Core.Friends;

/// <summary>One Steam friend who is playing DayZ, as the agent reports them.</summary>
public sealed class FriendInfo
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"ip:gameport" of the server they're on, or "" (main menu / private game details).</summary>
    public string Server { get; set; } = "";
}

/// <summary>Answer from GET /mm-agent/v1/friends?steamid=...</summary>
public sealed class FriendsReply
{
    public bool Ok { get; set; }
    /// <summary>Why it didn't work (private friends list, no key on the agent, ...).</summary>
    public string Message { get; set; } = "";
    public int FriendCount { get; set; }
    public List<FriendInfo> InDayZ { get; set; } = [];

    public const string Path = "/mm-agent/v1/friends";
}

/// <summary>Finds which Steam account is signed in on this PC, without starting anything.</summary>
public static class SteamAccount
{
    public const ulong Base = 76561197960265728UL;

    /// <summary>The signed-in account's 64-bit Steam ID, or null if it can't be found.</summary>
    public static ulong? Current(string? steamPath)
    {
        // Windows keeps the running Steam client's account here (32-bit account ID).
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null) is int id && id > 0)
                    return Base + (uint)id;
            }
            catch { }
        }
        // Otherwise: the account Steam remembers as most recent.
        return FromLoginUsers(steamPath == null ? null : Path.Combine(steamPath, "config", "loginusers.vdf"));
    }

    public static ulong? FromLoginUsers(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return null;
            var users = VdfNode.Parse(File.ReadAllText(path))["users"];
            if (users == null) return null;
            ulong? any = null;
            foreach (var (id, node) in users.Children)
            {
                if (!ulong.TryParse(id, out var sid) || !IsValid(sid)) continue;
                any ??= sid;
                if (node["MostRecent"]?.Value == "1" || node["mostrecent"]?.Value == "1") return sid;
            }
            return any;
        }
        catch { return null; }
    }

    /// <summary>The Steam display name of the signed-in account (from loginusers.vdf), or null.</summary>
    public static string? PersonaName(string? steamPath)
    {
        try
        {
            if (steamPath == null || Current(steamPath) is not { } me) return null;
            var path = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(path)) return null;
            var user = VdfNode.Parse(File.ReadAllText(path))["users"]?[me.ToString()];
            return user?["PersonaName"]?.Value is { Length: > 0 } n ? n : null;
        }
        catch { return null; }
    }

    /// <summary>A real individual-account Steam ID (7656119...).</summary>
    public static bool IsValid(ulong id) => id > Base && id < Base + 0x1_0000_0000UL;
}

/// <summary>
/// Servers friends have been seen on, kept on this PC (friends-history.jsonc) for 30 days.
/// Steam doesn't tell anyone where friends played in the past, so this only knows what the
/// launcher itself saw while it was open.
/// </summary>
public sealed class FriendHistory
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    public const int MaxServers = 100;

    public sealed class Seen
    {
        public string Server { get; set; } = "";       // "ip:gameport"
        public string ServerName { get; set; } = "";
        public DateTimeOffset LastSeen { get; set; }
        public List<string> Friends { get; set; } = []; // names, most recent first
    }

    public List<Seen> Servers { get; set; } = [];

    public static string FilePath => System.IO.Path.Combine(LauncherSettings.DataFolder, "friends-history.jsonc");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static FriendHistory Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<FriendHistory>(File.ReadAllText(FilePath), ReadOptions) is { } h)
            {
                h.Prune(DateTimeOffset.Now);
                return h;
            }
        }
        catch (JsonException) { }
        return new FriendHistory();
    }

    /// <summary>Records who is where right now. serverNames maps "ip:gameport" to a display name.</summary>
    public void Record(IEnumerable<FriendInfo> friends, Func<string, string?> serverName, DateTimeOffset now)
    {
        foreach (var group in friends.Where(f => f.Server.Length > 0).GroupBy(f => f.Server))
        {
            var entry = Servers.FirstOrDefault(s => s.Server == group.Key);
            if (entry == null) Servers.Add(entry = new Seen { Server = group.Key });
            entry.LastSeen = now;
            if (serverName(group.Key) is { Length: > 0 } n) entry.ServerName = n;
            foreach (var f in group)
            {
                entry.Friends.Remove(f.Name);
                entry.Friends.Insert(0, f.Name);
            }
            if (entry.Friends.Count > 20) entry.Friends.RemoveRange(20, entry.Friends.Count - 20);
        }
        Prune(now);
    }

    public void Prune(DateTimeOffset now)
    {
        Servers.RemoveAll(s => now - s.LastSeen > Keep);
        Servers = Servers.OrderByDescending(s => s.LastSeen).Take(MaxServers).ToList();
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("// MM Launcher: DayZ servers your Steam friends were seen on (the Friends tab, \"Seen recently\").");
            sb.AppendLine("// Only what this launcher saw while it was open; kept 30 days. Safe to delete.");
            sb.AppendLine("// Each entry: Server (ip:game port), ServerName, LastSeen, Friends (names, most recent first).");
            sb.Append(JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            sb.AppendLine();
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch { }
    }
}
