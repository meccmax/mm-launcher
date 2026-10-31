using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MMLauncher.Core.Browser;

/// <summary>
/// Every public DayZ server, as the MM Server Agent gets it from Valve's server list
/// (Steam Web API, IGameServersService/GetServerList) and passes on to launchers at
/// GET /mm-agent/v1/browser, gzip-compressed. Short property names keep ~10,000
/// servers to a few hundred KB.
/// </summary>
public sealed class BrowserFeed
{
    [JsonPropertyName("generated")] public DateTimeOffset Generated { get; set; }
    [JsonPropertyName("servers")] public List<BrowserEntry> Servers { get; set; } = [];

    /// <summary>How many entries Valve sent before filtering (to spot its 10,000-per-request limit).</summary>
    [JsonIgnore] public int RawCount { get; set; }

    public const string Path = "/mm-agent/v1/browser";
    public const int MaxBytes = 32 * 1024 * 1024; // decompressed; far above any real list

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public byte[] ToGzip()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(gz, this, Options);
        return ms.ToArray();
    }

    /// <summary>Reads gzip bytes; null if damaged or too large.</summary>
    public static BrowserFeed? FromGzip(byte[] gzip)
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
            var feed = JsonSerializer.Deserialize<BrowserFeed>(bounded, Options);
            if (feed == null) return null;
            feed.Servers = feed.Servers.Where(s => s.Valid).ToList();
            return feed;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses Valve's GetServerList reply into a feed (DayZ servers only).
    /// Throws JsonException if the reply isn't what Valve sends.
    /// </summary>
    public static BrowserFeed FromSteamReply(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var feed = new BrowserFeed { Generated = DateTimeOffset.UtcNow };
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            return feed; // Valve answers {"response":{}} when nothing matches
        foreach (var s in servers.EnumerateArray())
        {
            feed.RawCount++;
            if (s.TryGetProperty("appid", out var app) && app.TryGetInt32(out var appId) && appId != 221100) continue;
            var entry = new BrowserEntry
            {
                Address = Str(s, "addr"),
                GamePort = Int(s, "gameport"),
                Name = Clip(Str(s, "name"), 160),
                Map = Clip(Str(s, "map"), 40),
                Players = Int(s, "players"),
                MaxPlayers = Int(s, "max_players"),
                Keywords = Clip(Str(s, "gametype"), 400),
                Version = Clip(Str(s, "version"), 24),
            };
            if (entry.Valid) feed.Servers.Add(entry);
        }
        return feed;

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    }

    private static string Clip(string s, int max)
    {
        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max];
    }
}

/// <summary>One server in the feed.</summary>
public sealed class BrowserEntry
{
    /// <summary>"ip:queryport".</summary>
    [JsonPropertyName("a")] public string Address { get; set; } = "";
    [JsonPropertyName("g")] public int GamePort { get; set; }
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("m")] public string Map { get; set; } = "";
    [JsonPropertyName("p")] public int Players { get; set; }
    [JsonPropertyName("x")] public int MaxPlayers { get; set; }
    /// <summary>DayZ's keyword string: battleye, no3rd, lqs, etm, time, ...</summary>
    [JsonPropertyName("k")] public string Keywords { get; set; } = "";
    [JsonPropertyName("v")] public string Version { get; set; } = "";

    [JsonIgnore]
    public bool Valid => TrySplit(out _, out _) && Name.Length > 0;

    public bool TrySplit(out string ip, out int queryPort)
    {
        ip = "";
        queryPort = 0;
        int colon = Address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(Address.AsSpan(colon + 1), out queryPort) || queryPort is <= 0 or > 65535) return false;
        ip = Address[..colon];
        return System.Net.IPAddress.TryParse(ip, out _);
    }
}
