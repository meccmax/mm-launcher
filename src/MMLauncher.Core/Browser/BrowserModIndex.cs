using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MMLauncher.Core.DayZ;

namespace MMLauncher.Core.Browser;

/// <summary>
/// Which mods each modded server in the browser runs, so players can search the browser by mod
/// ("Terje", "Expansion") and filter for servers with AI. The MM Server Agent builds it by asking
/// every modded server for its mod list (the standard A2S_RULES query, which DayZ answers with
/// its mods) and serves it at GET /mm-agent/v1/browser-mods, gzip-compressed.
///
/// Each mod is listed once ("mods") and servers point at it by position ("servers"), which
/// keeps thousands of servers sharing the same popular mods small.
/// </summary>
public sealed partial class BrowserModIndex
{
    public const string Path = "/mm-agent/v1/browser-mods";
    public const int MaxBytes = 48 * 1024 * 1024; // decompressed; far above any real index
    public const int MaxModsPerServer = 400;
    public const int MaxNameLength = 80;

    [JsonPropertyName("generated")] public DateTimeOffset Generated { get; set; }
    [JsonPropertyName("mods")] public List<IndexedMod> Mods { get; set; } = [];
    /// <summary>"ip:queryport" → positions in <see cref="Mods"/>.</summary>
    [JsonPropertyName("servers")] public Dictionary<string, List<int>> Servers { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Builds the index from each server's mod list (agent side).</summary>
    public static BrowserModIndex Build(IEnumerable<KeyValuePair<string, IReadOnlyList<DayZServerMod>>> servers)
    {
        var index = new BrowserModIndex { Generated = DateTimeOffset.UtcNow };
        var positions = new Dictionary<ulong, int>();
        foreach (var (address, mods) in servers)
        {
            var list = new List<int>();
            foreach (var m in mods.Where(m => m.WorkshopId != 0).Take(MaxModsPerServer))
            {
                if (!positions.TryGetValue(m.WorkshopId, out var at))
                {
                    at = index.Mods.Count;
                    positions[m.WorkshopId] = at;
                    index.Mods.Add(new IndexedMod { Id = m.WorkshopId, Name = Clean(m.Name) });
                }
                else if (index.Mods[at].Name.Length == 0 && m.Name.Length > 0)
                    index.Mods[at].Name = Clean(m.Name);
                if (!list.Contains(at)) list.Add(at);
            }
            index.Servers[address] = list;
        }
        return index;
    }

    public byte[] ToGzip()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(gz, this, Options);
        return ms.ToArray();
    }

    /// <summary>Reads gzip bytes and checks every entry; null if damaged or too large.</summary>
    public static BrowserModIndex? FromGzip(byte[] gzip)
    {
        try
        {
            using var gz = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
            using var bounded = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = gz.Read(buffer)) > 0)
            {
                bounded.Write(buffer, 0, n);
                if (bounded.Length > MaxBytes) return null;
            }
            bounded.Position = 0;
            var index = JsonSerializer.Deserialize<BrowserModIndex>(bounded, Options);
            if (index == null) return null;
            index.Mods ??= [];
            foreach (var m in index.Mods) m.Name = Clean(m.Name);
            index.Servers = (index.Servers ?? new())
                .Where(s => s.Key.Length <= 64 && s.Value != null)
                .ToDictionary(s => s.Key, s => s.Value.Where(i => i >= 0 && i < index.Mods.Count).Distinct().Take(MaxModsPerServer).ToList(),
                              StringComparer.OrdinalIgnoreCase);
            return index;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static string Clean(string? s)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        return t.Length <= MaxNameLength ? t : t[..MaxNameLength];
    }

    // ------------------------------------------------------------------ launcher side

    private Dictionary<string, string>? _searchText;
    private HashSet<string>? _withAi;

    public bool Has(string address) => Servers.ContainsKey(address);

    /// <summary>The mods a server runs (empty if it isn't in the index).</summary>
    public IReadOnlyList<IndexedMod> ModsOf(string address) =>
        Servers.TryGetValue(address, out var list) ? list.Select(i => Mods[i]).ToList() : [];

    /// <summary>All of a server's mod names in one lower-case string, for fast searching.</summary>
    public string SearchTextOf(string address)
    {
        _searchText ??= Servers.ToDictionary(s => s.Key,
            s => string.Join("\n", s.Value.Select(i => Mods[i].Name.ToLowerInvariant())), StringComparer.OrdinalIgnoreCase);
        return _searchText.GetValueOrDefault(address, "");
    }

    /// <summary>The names of a server's mods that contain the word, for showing why it matched.</summary>
    public List<string> MatchingMods(string address, string word) =>
        ModsOf(address).Where(m => m.Name.Contains(word, StringComparison.OrdinalIgnoreCase)).Select(m => m.Name).ToList();

    /// <summary>Does the server run an AI mod (see <see cref="IsAiMod"/>)?</summary>
    public bool RunsAi(string address)
    {
        _withAi ??= Servers.Where(s => s.Value.Any(i => IsAiMod(Mods[i]))).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _withAi.Contains(address);
    }

    /// <summary>
    /// Mods that add AI players/bandits: DayZ-Expansion-AI, the Expansion Bundle (which includes
    /// it), and any mod with "AI", "eAI" or "Bandit(s)" as a word in its name.
    /// </summary>
    public static readonly IReadOnlySet<ulong> KnownAiMods = new HashSet<ulong>
    {
        2792982069, // DayZ-Expansion-AI
        2572331007, // DayZ-Expansion-Bundle (includes Expansion AI)
    };

    [GeneratedRegex(@"(?<![a-z])(ai|eai|bandits?)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AiWord();

    public static bool IsAiMod(IndexedMod m) => KnownAiMods.Contains(m.Id) || AiWord().IsMatch(m.Name);

    [GeneratedRegex(@"(?<![a-z])e?ai(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AiInName();

    /// <summary>A server name that says it has AI ("PvE AI", "[AI] Deer Isle").</summary>
    public static bool NameSaysAi(string serverName) => AiInName().IsMatch(serverName);
}

/// <summary>One mod in the index.</summary>
public sealed class IndexedMod
{
    [JsonPropertyName("i")] public ulong Id { get; set; }
    [JsonPropertyName("n")] public string Name { get; set; } = "";
}
