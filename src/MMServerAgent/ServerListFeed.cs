using System.Net;
using MMLauncher.Core.Browser;

namespace MMServerAgent;

/// <summary>
/// Keeps a fresh copy of Valve's list of every DayZ server for the launcher's
/// "All servers" browser. Asks the Steam Web API every few minutes with the key in
/// agent.jsonc (the key stays on this server), and keeps the last good list
/// (also on disk, so a restart serves it straight away).
/// </summary>
public sealed class ServerListFeed(AgentConfig config, Action<string> log, GeoIpUpdater? geo = null)
{
    public const string CacheFile = "server-list-cache.json.gz";
    /// <summary>Valve returns at most this many servers per request, whatever "limit" says.</summary>
    private const int SteamCap = 10000;
    private const int MaxRequests = 40;
    private volatile byte[]? _gzip;
    private int _count;
    private DateTime lastTruncationNote = DateTime.MinValue;

    public bool Enabled => config.SteamWebApiKey.Length > 0;

    /// <summary>The latest list, gzip-compressed JSON (null until the first fetch).</summary>
    public byte[]? Gzip => _gzip;
    public int Count => _count;

    /// <summary>The latest list itself (for the mod list crawler).</summary>
    public IReadOnlyList<BrowserEntry> Servers { get; private set; } = [];

    /// <summary>Raised after each fresh list from Steam.</summary>
    public event Action? Updated;

    /// <summary>MMAGENT_STEAM_API overrides Valve's address (testing).</summary>
    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("MMAGENT_STEAM_API") is { Length: > 0 } u ? u : "https://api.steampowered.com";

    private static string CachePath => Path.Combine(AppContext.BaseDirectory, CacheFile);

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            log("Server browser feed: off (no SteamWebApiKey in agent.jsonc)");
            return;
        }
        try
        {
            if (File.Exists(CachePath) && BrowserFeed.FromGzip(await File.ReadAllBytesAsync(CachePath, ct)) is { } cached)
            {
                _gzip = await File.ReadAllBytesAsync(CachePath, ct);
                _count = cached.Servers.Count;
                Servers = cached.Servers;
            }
        }
        catch { }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MMServerAgent");
        var every = TimeSpan.FromMinutes(Math.Max(2, config.ServerListRefreshMinutes));
        bool firstOk = true;
        while (!ct.IsCancellationRequested)
        {
            if (geo != null)
            {
                try { await geo.EnsureFreshAsync(http, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
            try
            {
                var feed = await FetchAsync(http, ct);
                if (geo?.Enabled == true)
                    foreach (var e in feed.Servers)
                        if (e.TrySplit(out var ip, out _)) e.Country = geo.Lookup(ip);
                var gz = feed.ToGzip();
                _gzip = gz;
                _count = feed.Servers.Count;
                Servers = feed.Servers;
                try { await File.WriteAllBytesAsync(CachePath, gz, ct); } catch { }
                if (firstOk) log($"Server browser feed: {feed.Servers.Count} DayZ servers from Steam ({gz.Length / 1024} KB), refreshing every {every.TotalMinutes:0} min" +
                                 (geo?.Enabled == true ? $"; {feed.Servers.Count(e => e.Country.Length > 0)} with a country" : ""));
                firstOk = false;
                try { Updated?.Invoke(); } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log("Server browser feed: couldn't get the list from Steam: " + ex.Message);
                firstOk = true; // log the next success again
            }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Valve answers at most 10,000 servers per request, and DayZ has more than that. So the
    /// list is asked for in parts: servers with players and empty ones, and any part that
    /// still hits the limit is split again by map (the biggest maps one by one, then
    /// "everything else"). Duplicates are dropped.
    /// </summary>
    private async Task<BrowserFeed> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var all = new Dictionary<string, BrowserEntry>(StringComparer.OrdinalIgnoreCase);
        int requests = 0;
        bool truncated = false;

        async Task CollectAsync(string filter, int depth)
        {
            if (requests >= MaxRequests) { truncated = true; return; }
            requests++;
            var part = await RequestAsync(http, filter, ct);
            foreach (var e in part.Servers) all.TryAdd(e.Address, e);
            if (part.RawCount < SteamCap - 50) return;
            if (depth >= 3) { truncated = true; return; }
            var maps = part.Servers.Where(e => e.Map.Length > 0 && !e.Map.Contains('\\'))
                                   .GroupBy(e => e.Map, StringComparer.OrdinalIgnoreCase)
                                   .OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key).ToList();
            if (maps.Count == 0) { truncated = true; return; }
            foreach (var m in maps) await CollectAsync($"{filter}\\map\\{m}", depth + 1);
            await CollectAsync($"{filter}\\nor\\{maps.Count}{string.Concat(maps.Select(m => $"\\map\\{m}"))}", depth + 1);
        }

        await CollectAsync(@"\appid\221100\empty\1", 0);      // servers with players
        await CollectAsync(@"\appid\221100\noplayers\1", 0);  // empty servers
        if (all.Count == 0) await CollectAsync(@"\appid\221100", 0); // in case Valve ignores those filters
        // Servers with players come in one request (a few thousand); only the long tail of empty
        // listings can be too big to fetch completely, which doesn't matter to players.
        if (truncated && lastTruncationNote < DateTime.UtcNow.AddHours(-24))
        {
            lastTruncationNote = DateTime.UtcNow;
            log($"Note: Steam lists more empty servers than the agent fetches ({all.Count} servers in {requests} requests). All servers with players are included; this is fine.");
        }
        return new BrowserFeed { Generated = DateTimeOffset.UtcNow, Servers = all.Values.ToList() };
    }

    private async Task<BrowserFeed> RequestAsync(HttpClient http, string filter, CancellationToken ct)
    {
        var url = $"{BaseUrl}/IGameServersService/GetServerList/v1/?key={Uri.EscapeDataString(config.SteamWebApiKey)}&filter={Uri.EscapeDataString(filter)}&limit={SteamCap}";
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        // Never put the URL (it holds the key) in a message.
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Steam rejected the SteamWebApiKey in agent.jsonc (check it at steamcommunity.com/dev/apikey).");
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Steam answered {(int)res.StatusCode} {res.ReasonPhrase}.");
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        return BrowserFeed.FromSteamReply(body);
    }
}
