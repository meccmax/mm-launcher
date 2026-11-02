using System.Collections.Concurrent;
using System.Net;
using MMLauncher.Core.Config;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Query;
using MMLauncher.Core.Services;

namespace MMLauncher.Core.Browser;

public enum PerspectiveFilter { Any, FirstPersonOnly, ThirdPersonAllowed }
public enum ModFilter { Any, Modded, Vanilla }
public enum BrowserSort { Players, Name, Map, Ping }
/// <summary>How many players are on right now: any, at least one, 1-14, 15-49, 50 or more.</summary>
public enum PlayerRange { Any, NotEmpty, Few, Medium, Busy }
/// <summary>PvE only, or everything else (PvP, PvPvE and servers that don't say).</summary>
public enum ModeFilter { Any, PvEOnly, PvP }

/// <summary>What the player picked in the browser's filter bar.</summary>
public sealed record BrowserFilter(
    string Search = "",
    string? Map = null,
    PerspectiveFilter Perspective = PerspectiveFilter.Any,
    bool HideEmpty = false,
    bool HideFull = false,
    ModFilter Mods = ModFilter.Any,
    BrowserSort Sort = BrowserSort.Players,
    PlayerRange PlayerCount = PlayerRange.Any,
    ModeFilter Mode = ModeFilter.Any,
    /// <summary>Country code ("DE") or continent ("@EU"); null = anywhere.</summary>
    string? Country = null,
    /// <summary>Only servers running an AI mod (from the mod index) or with AI in their name.</summary>
    bool HasAi = false);

/// <summary>
/// The "All servers" list: gets Valve's full DayZ server list through an MM agent,
/// keeps a copy on disk so the browser opens instantly, filters and sorts it, and
/// measures ping for the servers the player is actually looking at.
/// </summary>
public sealed class BrowserService(LauncherService service)
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(3);
    private static string CachePath => Path.Combine(LauncherSettings.DataFolder, "browser-cache.json.gz");
    private static string ModsCachePath => Path.Combine(LauncherSettings.DataFolder, "browser-mods-cache.json.gz");
    /// <summary>Mod lists change slowly (the agent collects them every half hour or so).</summary>
    public static readonly TimeSpan ModsRefreshAfter = TimeSpan.FromMinutes(20);

    /// <summary>Which mods each modded server runs, for mod search and the AI filter (null until an agent sends it).</summary>
    public BrowserModIndex? Mods { get; private set; }
    public DateTimeOffset? ModsFetchedAt { get; private set; }
    public bool ModsStale => ModsFetchedAt is not { } t || DateTimeOffset.Now - t > ModsRefreshAfter;

    public BrowserFeed? Feed { get; private set; }
    public DateTimeOffset? FetchedAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Ping results: "ip:queryport" -> (ms, when). Missing = not measured; -1 = no answer.</summary>
    public ConcurrentDictionary<string, (int Ms, DateTime At)> Pings { get; } = new();

    public void LoadCache()
    {
        try
        {
            if (File.Exists(CachePath) && BrowserFeed.FromGzip(File.ReadAllBytes(CachePath)) is { } feed)
            {
                Feed = feed;
                FetchedAt = feed.Generated;
            }
        }
        catch { }
        try
        {
            if (File.Exists(ModsCachePath) && BrowserModIndex.FromGzip(File.ReadAllBytes(ModsCachePath)) is { } mods)
            {
                Mods = mods;
                ModsFetchedAt = mods.Generated;
            }
        }
        catch { }
    }

    /// <summary>Gets fresh mod lists from the first agent that has them. False if none could (older agents don't).</summary>
    public async Task<bool> RefreshModsAsync(CancellationToken ct = default)
    {
        if (service.LastAgents.BrowserAgents.Count == 0) await service.QueryAgentsAsync(ct);
        foreach (var (host, port) in service.LastAgents.BrowserAgents.ToList())
        {
            var index = await service.Agents.GetBrowserModsAsync(host, port, ct);
            if (index == null || index.Servers.Count == 0) continue;
            Mods = index;
            ModsFetchedAt = DateTimeOffset.Now;
            try { await File.WriteAllBytesAsync(ModsCachePath, index.ToGzip(), ct); } catch { }
            Log.Info($"Server browser: mod lists for {index.Servers.Count} servers ({index.Mods.Count} mods) from the agent at {host}:{port}.");
            return true;
        }
        ModsFetchedAt = DateTimeOffset.Now; // don't ask again on every refresh when no agent has them
        return false;
    }

    public bool IsStale => FetchedAt is not { } t || DateTimeOffset.Now - t > RefreshAfter;

    /// <summary>Gets a fresh list from the first agent that has one. False (with LastError) if none could.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        var agents = service.LastAgents.BrowserAgents;
        if (agents.Count == 0)
        {
            await service.QueryAgentsAsync(ct);
            agents = service.LastAgents.BrowserAgents;
        }
        if (agents.Count == 0)
        {
            LastError = "No MM server agent is sharing the server list right now.";
            return false;
        }
        foreach (var (host, port) in agents)
        {
            var feed = await service.Agents.GetBrowserFeedAsync(host, port, ct);
            if (feed == null || feed.Servers.Count == 0) continue;
            Feed = feed;
            FetchedAt = DateTimeOffset.Now;
            LastError = null;
            try { await File.WriteAllBytesAsync(CachePath, feed.ToGzip(), ct); } catch { }
            Log.Info($"Server browser: {feed.Servers.Count} servers from the agent at {host}:{port}.");
            return true;
        }
        LastError = "The MM server agent couldn't send the server list. Try again in a minute.";
        return false;
    }

    // ------------------------------------------------------------------ filtering

    /// <summary>Applies the filter bar to a list. Pure, so it's easy to test.</summary>
    public static List<BrowserEntry> Apply(IEnumerable<BrowserEntry> servers, BrowserFilter f, Func<BrowserEntry, int?>? pingOf = null, BrowserModIndex? mods = null)
    {
        var words = f.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lowerWords = words.Select(w => w.ToLowerInvariant()).ToArray();
        var query = servers.Where(s =>
        {
            if (f.Map != null && !s.Map.Equals(f.Map, StringComparison.OrdinalIgnoreCase)) return false;
            if (f.HideEmpty && s.Players == 0) return false;
            if (f.HideFull && s.MaxPlayers > 0 && s.Players >= s.MaxPlayers) return false;
            if (!InRange(s.Players, f.PlayerCount)) return false;
            if (f.Mode != ModeFilter.Any && (GameModes.Classify(s.Name, s.Keywords) == GameMode.PvE) != (f.Mode == ModeFilter.PvEOnly)) return false;
            if (!Countries.Matches(f.Country, s.Country)) return false;
            if (f.HasAi && !BrowserModIndex.NameSaysAi(s.Name) && mods?.RunsAi(s.Address) != true) return false;
            if (f.Perspective != PerspectiveFilter.Any)
            {
                bool firstOnly = HasTag(s.Keywords, "no3rd");
                if (f.Perspective == PerspectiveFilter.FirstPersonOnly != firstOnly) return false;
            }
            if (f.Mods != ModFilter.Any)
            {
                bool modded = HasTag(s.Keywords, "mod");
                if (f.Mods == ModFilter.Modded != modded) return false;
            }
            for (int i = 0; i < words.Length; i++)
            {
                var w = words[i];
                if (!s.Name.Contains(w, StringComparison.OrdinalIgnoreCase) &&
                    !s.Map.Contains(w, StringComparison.OrdinalIgnoreCase) &&
                    !s.Address.StartsWith(w, StringComparison.OrdinalIgnoreCase) &&
                    !(mods != null && mods.SearchTextOf(s.Address).Contains(lowerWords[i], StringComparison.Ordinal)))
                    return false;
            }
            return true;
        });

        query = f.Sort switch
        {
            BrowserSort.Name => query.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
            BrowserSort.Map => query.OrderBy(s => s.Map, StringComparer.OrdinalIgnoreCase).ThenByDescending(s => s.Players),
            BrowserSort.Ping => query.OrderBy(s => pingOf?.Invoke(s) is int p and >= 0 ? p : int.MaxValue).ThenByDescending(s => s.Players),
            _ => query.OrderByDescending(s => s.Players).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
        };
        return query.ToList();
    }

    public static bool InRange(int players, PlayerRange range) => range switch
    {
        PlayerRange.NotEmpty => players >= 1,
        PlayerRange.Few => players is >= 1 and <= 14,
        PlayerRange.Medium => players is >= 15 and <= 49,
        PlayerRange.Busy => players >= 50,
        _ => true,
    };

    /// <summary>
    /// Continents, then countries, with how many servers are in each (most first). Values are
    /// "@EU"-style for continents and country codes for countries.
    /// </summary>
    public static List<(string Value, string Label, int Count)> CountryChoices(IEnumerable<BrowserEntry> servers)
    {
        var byCountry = servers.Where(s => s.Country.Length == 2)
                               .GroupBy(s => s.Country.ToUpperInvariant())
                               .Select(g => (Code: g.Key, Count: g.Count())).ToList();
        var list = new List<(string, string, int)>();
        foreach (var (code, name) in Countries.Continents)
        {
            int n = byCountry.Where(c => Countries.Continent(c.Code) == code).Sum(c => c.Count);
            if (n > 0) list.Add((Countries.ContinentPrefix + code, name, n));
        }
        list.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        foreach (var c in byCountry.OrderByDescending(c => c.Count).ThenBy(c => Countries.Name(c.Code)))
            list.Add((c.Code, Countries.Name(c.Code), c.Count));
        return list;
    }

    public static bool HasTag(string keywords, string tag)
    {
        foreach (var t in keywords.Split(','))
            if (t.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Maps in the list with how many servers run each, most common first.</summary>
    public static List<(string Map, int Count)> Maps(IEnumerable<BrowserEntry> servers) =>
        servers.Where(s => s.Map.Length > 0)
               .GroupBy(s => s.Map.ToLowerInvariant())
               .Select(g => (g.First().Map, g.Count()))
               .OrderByDescending(m => m.Item2)
               .ToList();

    // ------------------------------------------------------------------ ping

    /// <summary>
    /// Measures ping (A2S_INFO round trip) for the given servers, a few dozen at a time,
    /// skipping ones measured in the last few minutes. Calls back with each result.
    /// </summary>
    public async Task PingAsync(IEnumerable<BrowserEntry> servers, Action<BrowserEntry, int, A2sInfo?> onResult, CancellationToken ct)
    {
        var due = servers.Where(s => !Pings.TryGetValue(s.Address, out var p) || DateTime.UtcNow - p.At > TimeSpan.FromMinutes(5)).ToList();
        using var gate = new SemaphoreSlim(48);
        var tasks = due.Select(async s =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!s.TrySplit(out var ip, out var port)) return;
                int ms = -1;
                A2sInfo? info = null;
                try
                {
                    (info, ms) = await A2sClient.QueryInfoAsync(new IPEndPoint(IPAddress.Parse(ip), port), TimeSpan.FromSeconds(1.5), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
                Pings[s.Address] = (ms, DateTime.UtcNow);
                onResult(s, ms, info);
            }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }
}
