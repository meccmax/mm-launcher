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

/// <summary>What the player picked in the browser's filter bar.</summary>
public sealed record BrowserFilter(
    string Search = "",
    string? Map = null,
    PerspectiveFilter Perspective = PerspectiveFilter.Any,
    bool HideEmpty = false,
    bool HideFull = false,
    ModFilter Mods = ModFilter.Any,
    BrowserSort Sort = BrowserSort.Players);

/// <summary>
/// The "All servers" list: gets Valve's full DayZ server list through an MM agent,
/// keeps a copy on disk so the browser opens instantly, filters and sorts it, and
/// measures ping for the servers the player is actually looking at.
/// </summary>
public sealed class BrowserService(LauncherService service)
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(3);
    private static string CachePath => Path.Combine(LauncherSettings.DataFolder, "browser-cache.json.gz");

    public BrowserFeed? Feed { get; private set; }
    public DateTimeOffset? FetchedAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Ping results: "ip:queryport" -> (ms, when). Missing = not measured; -1 = no answer.</summary>
    public ConcurrentDictionary<string, (int Ms, DateTime At)> Pings { get; } = new();

    public void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var feed = BrowserFeed.FromGzip(File.ReadAllBytes(CachePath));
            if (feed == null) return;
            Feed = feed;
            FetchedAt = feed.Generated;
        }
        catch { }
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
    public static List<BrowserEntry> Apply(IEnumerable<BrowserEntry> servers, BrowserFilter f, Func<BrowserEntry, int?>? pingOf = null)
    {
        var words = f.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var query = servers.Where(s =>
        {
            if (f.Map != null && !s.Map.Equals(f.Map, StringComparison.OrdinalIgnoreCase)) return false;
            if (f.HideEmpty && s.Players == 0) return false;
            if (f.HideFull && s.MaxPlayers > 0 && s.Players >= s.MaxPlayers) return false;
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
            foreach (var w in words)
                if (!s.Name.Contains(w, StringComparison.OrdinalIgnoreCase) &&
                    !s.Map.Contains(w, StringComparison.OrdinalIgnoreCase) &&
                    !s.Address.StartsWith(w, StringComparison.OrdinalIgnoreCase))
                    return false;
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
