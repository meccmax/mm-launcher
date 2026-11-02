using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using MMLauncher.Core.Browser;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Query;

namespace MMServerAgent;

/// <summary>
/// Collects the mod list of every modded server in the browser list, so launchers can search by
/// mod and filter for AI. It sends each server the standard A2S_RULES query (what any server
/// browser sends to show a server's mods), reads DayZ's mod list from the reply with our own
/// parser, and serves the result as a <see cref="BrowserModIndex"/>.
///
/// It goes gently, so the game servers on this machine never notice: at most
/// <see cref="Parallel"/> queries at once and <see cref="PerSecond"/> new ones a second
/// (roughly 2-3 Mbit/s while it runs). A round over ~8,000 servers takes a few minutes and
/// repeats every "ModListRefreshMinutes".
/// </summary>
public sealed class ModListCrawler(AgentConfig config, ServerListFeed feed, Action<string> log)
{
    public const string CacheFile = "mod-index-cache.json.gz";
    private const int Parallel = 24;
    private const int PerSecond = 40;
    private const int MaxServers = 15000;
    /// <summary>A server that stops answering keeps its last known mods for this long.</summary>
    private static readonly TimeSpan KeepUnanswered = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, (IReadOnlyList<DayZServerMod> Mods, DateTime At)> _known = new(StringComparer.OrdinalIgnoreCase);
    private volatile byte[]? _gzip;
    private int _servers, _mods;

    public bool Enabled => config.ModSearch && feed.Enabled;
    public byte[]? Gzip => _gzip;
    public int ServerCount => _servers;
    public int ModCount => _mods;

    private static string CachePath => Path.Combine(AppContext.BaseDirectory, CacheFile);

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            log(config.ModSearch ? "Mod search: off (needs the server browser feed, i.e. a SteamWebApiKey)" : "Mod search: off (ModSearch is false in agent.jsonc)");
            return;
        }
        LoadCache();
        var every = TimeSpan.FromMinutes(config.ModListRefreshMinutes);
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            // Wait for the first server list from Steam.
            while (feed.Servers.Count == 0 && !ct.IsCancellationRequested)
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
            try
            {
                await CrawlAsync(first, ct);
                first = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log("Mod search: collecting mod lists failed: " + ex.Message); }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var bytes = File.ReadAllBytes(CachePath);
            if (BrowserModIndex.FromGzip(bytes) is not { } index) return;
            var at = index.Generated.UtcDateTime;
            foreach (var (address, positions) in index.Servers)
                _known[address] = (positions.Select(i => new DayZServerMod(index.Mods[i].Id, index.Mods[i].Name, 0)).ToList(), at);
            _gzip = bytes;
            _servers = index.Servers.Count;
            _mods = index.Mods.Count;
        }
        catch { }
    }

    private async Task CrawlAsync(bool logDetails, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Modded servers only (vanilla ones have no mods to find), busiest first.
        var targets = feed.Servers.Where(s => BrowserService.HasTag(s.Keywords, "mod"))
                                  .OrderByDescending(s => s.Players).Take(MaxServers).ToList();
        int answered = 0;
        using var gate = new SemaphoreSlim(Parallel);
        var tasks = new List<Task>(targets.Count);
        var spacing = TimeSpan.FromSeconds(1.0 / PerSecond);
        foreach (var s in targets)
        {
            await gate.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (!s.TrySplit(out var ip, out var port)) return;
                    var rules = await A2sClient.QueryRulesAsync(new IPEndPoint(IPAddress.Parse(ip), port), TimeSpan.FromSeconds(2), ct, attempts: 1);
                    var parsed = DayZRulesParser.Parse(rules);
                    _known[s.Address] = (parsed.Mods, DateTime.UtcNow);
                    Interlocked.Increment(ref answered);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception) { } // no answer, closed port, odd reply: keep what we had
                finally { gate.Release(); }
            }, ct));
            await Task.Delay(spacing, ct);
        }
        await Task.WhenAll(tasks);

        // Keep servers still in the list that answered recently.
        var listed = feed.Servers.Select(s => s.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _known.Keys)
            if (!listed.Contains(key) || DateTime.UtcNow - _known[key].At > KeepUnanswered) _known.TryRemove(key, out _);

        var index = BrowserModIndex.Build(_known.Select(k => new KeyValuePair<string, IReadOnlyList<DayZServerMod>>(k.Key, k.Value.Mods)));
        var gz = index.ToGzip();
        _gzip = gz;
        _servers = index.Servers.Count;
        _mods = index.Mods.Count;
        try { await File.WriteAllBytesAsync(CachePath, gz, ct); } catch { }
        if (logDetails)
            log($"Mod search: mod lists from {answered:N0} of {targets.Count:N0} modded servers ({_mods:N0} different mods, {gz.Length / 1024} KB) in {sw.Elapsed.TotalMinutes:0.0} min; repeating every {config.ModListRefreshMinutes} min");
    }
}
