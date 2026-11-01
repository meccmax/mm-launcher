using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MMLauncher.Core.Agent;
using MMLauncher.Core.History;
using MMLauncher.Core.Query;

namespace MMServerAgent;

/// <summary>
/// Every 5 minutes, asks each running DayZ server on this machine how many players it has
/// (the normal Steam status query) and keeps 7 days of it in player-history.jsonc. Launchers
/// draw it as the PLAYERS chart. A server started with -ip= only answers on that address, so
/// besides 127.0.0.1 every address of this machine is tried, and the one that works is remembered.
/// </summary>
public sealed class PlayerHistoryRecorder(Action<string> log)
{
    public const string FileName = "player-history.jsonc";
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly PlayerHistoryStore _store = PlayerHistoryStore.Load(FilePath);
    private readonly ConcurrentDictionary<int, string> _json = new();
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<int, IPAddress> _answersOn = new();
    private readonly ConcurrentDictionary<int, bool> _warned = new();

    // Up to ~2000 numbers per answer: no indenting.
    private static readonly JsonSerializerOptions Compact = new(AgentResponse.Json) { WriteIndented = false };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>True when there's at least one real reading for the server on this game port.</summary>
    public bool Has(int gamePort)
    {
        lock (_lock) return _store.Servers.TryGetValue(gamePort, out var samples) && samples.Values.Any(v => v >= 0);
    }

    /// <summary>Takes a reading of every running server when the last one is 5 minutes old (or there's none yet).</summary>
    public Task SampleIfDueAsync(IReadOnlyList<AgentServerInfo> running)
    {
        var now = DateTimeOffset.Now;
        return now - _lastSample >= Every ? SampleAsync(running, now) : Task.CompletedTask;
    }

    public async Task SampleAsync(IReadOnlyList<AgentServerInfo> running, DateTimeOffset now)
    {
        _lastSample = now;
        var addresses = LocalAddresses();
        var readings = await Task.WhenAll(running.Select(async s =>
        {
            var (info, at) = await QueryAsync(s.GamePort, s.QueryPort, addresses);
            if (info != null && at != null)
            {
                if (!_answersOn.TryGetValue(s.GamePort, out var before) || !before.Equals(at))
                {
                    _answersOn[s.GamePort] = at;
                    if (!at.Equals(IPAddress.Loopback) || _warned.ContainsKey(s.GamePort))
                        log($"Player history: port {s.GamePort} answers on {at}:{s.QueryPort}.");
                }
                _warned.TryRemove(s.GamePort, out _);
                return (s.GamePort, Players: (int?)info.Players, Max: info.MaxPlayers > 0 ? info.MaxPlayers : s.MaxPlayers);
            }
            if (_warned.TryAdd(s.GamePort, true))
                log($"Player history: port {s.GamePort} didn't answer a status query on query port {s.QueryPort} at 127.0.0.1 or " +
                    $"{(addresses.Count == 0 ? "any other address" : string.Join(", ", addresses))}; recorded as down until it does.");
            return (s.GamePort, Players: (int?)null, Max: s.MaxPlayers);
        }));
        lock (_lock)
        {
            foreach (var r in readings) _store.Record(r.GamePort, now, r.Players, r.Max);
            // Known servers that aren't running right now: down.
            foreach (var port in _store.Servers.Keys.Except(readings.Select(r => r.GamePort)).ToList())
                _store.Record(port, now, null, 0);
            _json.Clear();
            try { _store.Save(FilePath); }
            catch (Exception ex) { log("Player history: couldn't save " + FileName + ": " + ex.Message); }
        }
    }

    /// <summary>Tries the address that worked last time for this server, then 127.0.0.1, then this machine's other addresses.</summary>
    private async Task<(A2sInfo? Info, IPAddress? At)> QueryAsync(int gamePort, int queryPort, IReadOnlyList<IPAddress> addresses)
    {
        var order = new List<IPAddress>();
        if (_answersOn.TryGetValue(gamePort, out var known)) order.Add(known);
        foreach (var a in new[] { IPAddress.Loopback }.Concat(addresses))
            if (!order.Contains(a)) order.Add(a);
        // All at once, so a server that answers nowhere costs one timeout, not one per address.
        var tries = await Task.WhenAll(order.Select(async a =>
        {
            try { return (await A2sClient.QueryInfoAsync(new IPEndPoint(a, queryPort), TimeSpan.FromSeconds(2))).Info; }
            catch (Exception) { return null; }
        }));
        for (int i = 0; i < order.Count; i++)
            if (tries[i] != null) return (tries[i], order[i]);
        return (null, null);
    }

    /// <summary>This machine's own IPv4 addresses (network cards that are up), not counting 127.0.0.1.</summary>
    private static List<IPAddress> LocalAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Distinct().Take(16).ToList();
        }
        catch (Exception) { return []; }
    }

    public HttpReply Handle(string query)
    {
        var q = System.Web.HttpUtility.ParseQueryString(query);
        if (!int.TryParse(q["port"], out var port) || port is <= 0 or > 65535)
            return new HttpReply(400, "text/plain", "Bad port.");
        var json = _json.GetOrAdd(port, p =>
        {
            PlayerHistoryReply? reply;
            lock (_lock) reply = _store.Reply(p, DateTimeOffset.Now);
            return reply == null ? "" : JsonSerializer.Serialize(reply, Compact);
        });
        return json.Length == 0 ? new HttpReply(404, "text/plain", "No history for that port.") : new HttpReply(200, "application/json", json);
    }
}
