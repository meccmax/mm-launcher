using System.Text;
using System.Text.Json;

namespace MMLauncher.Core.History;

/// <summary>
/// Answer from GET /mm-agent/v1/history?port=&lt;game port&gt;: one server's player count every
/// few minutes for the last 7 days. Players[i] is the count at Start + i * StepSeconds
/// (Unix seconds, UTC); -1 means no reading (server down or restarting).
/// </summary>
public sealed class PlayerHistoryReply
{
    public const string Path = "/mm-agent/v1/history";
    public const int DefaultStepSeconds = 300;
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    public const int MaxSamples = 7 * 24 * 12 * 2; // generous: 7 days at 2.5-minute steps

    public int GamePort { get; set; }
    public long Start { get; set; }
    public int StepSeconds { get; set; } = DefaultStepSeconds;
    public int MaxPlayers { get; set; }
    public List<int> Players { get; set; } = [];

    public DateTimeOffset TimeAt(int index) => DateTimeOffset.FromUnixTimeSeconds(Start + (long)index * StepSeconds);

    /// <summary>Everything from an agent is checked: sane step, length and values.</summary>
    public static PlayerHistoryReply? Sanitize(PlayerHistoryReply? r)
    {
        if (r == null || r.StepSeconds is < 60 or > 3600 || r.Start <= 0) return null;
        return new PlayerHistoryReply
        {
            GamePort = r.GamePort,
            Start = r.Start,
            StepSeconds = r.StepSeconds,
            MaxPlayers = Math.Clamp(r.MaxPlayers, 0, 1000),
            Players = (r.Players ?? []).TakeLast(MaxSamples).Select(p => p < 0 ? -1 : Math.Min(p, 1000)).ToList(),
        };
    }
}

/// <summary>What the launcher says about a server's history ("usually busiest 8–11 PM").</summary>
public sealed record HistorySummary(int? PeakPlayers, DateTimeOffset? PeakAt, string BusiestText, double? Average, int Readings);

public static class HistoryStats
{
    /// <summary>Peak and average over the samples from 'from' on; busiest 3-hour stretch of the day over all 7 days.</summary>
    public static HistorySummary Summarize(PlayerHistoryReply h, DateTimeOffset from, TimeZoneInfo zone)
    {
        int? peak = null; DateTimeOffset? peakAt = null; double sum = 0; int n = 0;
        for (int i = 0; i < h.Players.Count; i++)
        {
            var p = h.Players[i];
            if (p < 0) continue;
            var t = h.TimeAt(i);
            if (t < from) continue;
            sum += p; n++;
            if (peak == null || p > peak) { peak = p; peakAt = t; }
        }
        return new HistorySummary(peak, peakAt, BusiestHours(h, zone), n > 0 ? sum / n : null, n);
    }

    /// <summary>"Usually busiest 8 PM – 11 PM" from the hourly averages, or "" with too little data.</summary>
    public static string BusiestHours(PlayerHistoryReply h, TimeZoneInfo zone)
    {
        var sums = new double[24];
        var counts = new int[24];
        for (int i = 0; i < h.Players.Count; i++)
        {
            if (h.Players[i] < 0) continue;
            int hour = TimeZoneInfo.ConvertTime(h.TimeAt(i), zone).Hour;
            sums[hour] += h.Players[i];
            counts[hour]++;
        }
        // Needs most hours of the day covered, or the "busiest" time is just when we happened to look.
        if (counts.Count(c => c > 0) < 18) return "";
        var avg = Enumerable.Range(0, 24).Select(i => counts[i] > 0 ? sums[i] / counts[i] : 0).ToArray();
        if (avg.Max() < 1) return "";
        int best = 0; double bestSum = -1;
        for (int start = 0; start < 24; start++)
        {
            double s = avg[start] + avg[(start + 1) % 24] + avg[(start + 2) % 24];
            if (s > bestSum) { bestSum = s; best = start; }
        }
        return $"Usually busiest {HourText(best)} – {HourText((best + 3) % 24)}";
    }

    private static string HourText(int hour) =>
        new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The agent's record of player counts, kept in player-history.jsonc next to the agent.
/// One reading per server every StepSeconds, for 7 days.
/// </summary>
public sealed class PlayerHistoryStore
{
    public int StepSeconds { get; set; } = PlayerHistoryReply.DefaultStepSeconds;
    /// <summary>Game port → slot (Unix seconds / StepSeconds) → players (-1 = down).</summary>
    public Dictionary<int, SortedDictionary<long, int>> Servers { get; set; } = new();
    public Dictionary<int, int> MaxPlayers { get; set; } = new();

    public void Record(int gamePort, DateTimeOffset at, int? players, int maxPlayers)
    {
        long slot = at.ToUnixTimeSeconds() / StepSeconds;
        if (!Servers.TryGetValue(gamePort, out var samples)) Servers[gamePort] = samples = new SortedDictionary<long, int>();
        // Two readings in one slot (a restart of the agent): keep the higher; "down" never hides a reading.
        int value = players is { } p ? Math.Max(0, p) : -1;
        samples[slot] = samples.TryGetValue(slot, out var old) ? Math.Max(old, value) : value;
        if (maxPlayers > 0) MaxPlayers[gamePort] = maxPlayers;
        Prune(at);
    }

    public void Prune(DateTimeOffset now)
    {
        long oldest = (now - PlayerHistoryReply.Keep).ToUnixTimeSeconds() / StepSeconds;
        foreach (var (port, samples) in Servers.ToList())
        {
            foreach (var slot in samples.Keys.Where(s => s < oldest).ToList()) samples.Remove(slot);
            // A server that hasn't had a single real reading for a week is gone.
            if (samples.Values.All(v => v < 0)) { Servers.Remove(port); MaxPlayers.Remove(port); }
        }
    }

    /// <summary>The last 7 days for one server as an evenly spaced series (gaps are -1), or null.</summary>
    public PlayerHistoryReply? Reply(int gamePort, DateTimeOffset now)
    {
        if (!Servers.TryGetValue(gamePort, out var samples) || samples.Count == 0) return null;
        long last = now.ToUnixTimeSeconds() / StepSeconds;
        long first = Math.Max(samples.Keys.First(), last - (long)(PlayerHistoryReply.Keep.TotalSeconds / StepSeconds) + 1);
        var players = new List<int>((int)Math.Max(0, last - first + 1));
        for (long s = first; s <= last; s++) players.Add(samples.TryGetValue(s, out var v) ? v : -1);
        return new PlayerHistoryReply
        {
            GamePort = gamePort,
            Start = first * StepSeconds,
            StepSeconds = StepSeconds,
            MaxPlayers = MaxPlayers.GetValueOrDefault(gamePort),
            Players = players,
        };
    }

    public static PlayerHistoryStore Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<PlayerHistoryStore>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                }) is { StepSeconds: >= 60 and <= 3600 } store)
            {
                store.Prune(DateTimeOffset.Now);
                return store;
            }
        }
        catch (Exception) { }
        return new PlayerHistoryStore();
    }

    public void Save(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// MM Server Agent: player counts it recorded, for the launcher's PLAYERS chart.");
        sb.AppendLine("// One reading per server every StepSeconds (300 = 5 minutes), kept for 7 days.");
        sb.AppendLine("// Servers: game port -> { time slot (Unix time / StepSeconds): players, -1 = server down }.");
        sb.AppendLine("// MaxPlayers: game port -> the server's player slots. Written by the agent; safe to delete (history starts over).");
        sb.Append(JsonSerializer.Serialize(this));
        sb.AppendLine();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, path, overwrite: true);
    }
}
