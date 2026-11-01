using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MMLauncher.Core.Agent;
using MMLauncher.Core.History;
using MMLauncher.Core.Query;

namespace MMServerAgent;

/// <summary>
/// Every 5 minutes, asks each running DayZ server on this machine how many players it has
/// (the normal Steam status query, on 127.0.0.1) and keeps 7 days of it in
/// player-history.jsonc. Launchers draw it as the PLAYERS chart.
/// </summary>
public sealed class PlayerHistoryRecorder(Action<string> log)
{
    public const string FileName = "player-history.jsonc";
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly PlayerHistoryStore _store = PlayerHistoryStore.Load(FilePath);
    private readonly ConcurrentDictionary<int, string> _json = new();
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
    private bool _warnedQuery;

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
        var readings = await Task.WhenAll(running.Select(async s =>
        {
            try
            {
                var (info, _) = await A2sClient.QueryInfoAsync(new IPEndPoint(IPAddress.Loopback, s.QueryPort), TimeSpan.FromSeconds(3));
                return (s.GamePort, Players: (int?)info.Players, Max: info.MaxPlayers > 0 ? info.MaxPlayers : s.MaxPlayers);
            }
            catch (Exception ex)
            {
                if (!_warnedQuery) { _warnedQuery = true; log($"Player history: port {s.GamePort} didn't answer a status query on 127.0.0.1:{s.QueryPort} ({ex.Message}); recorded as down."); }
                return (s.GamePort, Players: (int?)null, Max: s.MaxPlayers);
            }
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
