using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MMLauncher.Core.Agent;
using MMLauncher.Core.Usage;

namespace MMServerAgent;

/// <summary>
/// Takes anonymous usage stats from launchers whose players said yes, and keeps them in the
/// "usage" folder next to the agent: one usage-YYYY-MM-DD.jsonl file per day (one event per line)
/// plus summary.txt, a readable overview of the last 7 and 30 days. The sender's IP address is
/// only used for the spam limit, never stored.
/// </summary>
public sealed class UsageReceiver(AgentConfig config, Action<string> log)
{
    public const string FolderName = "usage";
    public const string SummaryFile = "summary.txt";
    private const int MaxPerAddressPerHour = 60;
    private const int MaxPerDay = 20_000;
    private static readonly TimeSpan SummaryEvery = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, List<DateTime>> _perAddress = new();
    private readonly List<DateTime> _all = [];
    private readonly object _gate = new();
    private readonly object _fileLock = new();
    private DateTime _lastSummary = DateTime.MinValue;
    private DateTime _lastPrune = DateTime.MinValue;
    private int _summaryRunning;

    public static string FolderPath => Path.Combine(AppContext.BaseDirectory, FolderName);
    public bool Enabled => config.AcceptUsageStats;

    private static readonly JsonSerializerOptions Line = new(AgentResponse.Json) { WriteIndented = false };

    /// <summary>Checked before the body is read: on, small enough, and not flooding.</summary>
    public HttpReply? Admit(System.Net.IPAddress remote, long length)
    {
        if (!config.AcceptUsageStats) return new HttpReply(403, "text/plain", "Usage stats are off on this server.");
        if (length > UsageBatch.MaxBytes) return new HttpReply(413, "text/plain", "Too large.");
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_perAddress.Count > 10_000) _perAddress.Clear();
            var mine = _perAddress.GetOrAdd(remote.ToString(), _ => []);
            mine.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            _all.RemoveAll(t => now - t > TimeSpan.FromDays(1));
            if (mine.Count >= MaxPerAddressPerHour || _all.Count >= MaxPerDay) return new HttpReply(429, "text/plain", "Slow down.");
            mine.Add(now);
            _all.Add(now);
        }
        return null;
    }

    public HttpReply Handle(HttpRequestInfo request)
    {
        if (!config.AcceptUsageStats) return new HttpReply(403, "text/plain", "Usage stats are off on this server.");
        if (request.Body.Length > UsageBatch.MaxBytes) return new HttpReply(413, "text/plain", "Too large.");
        UsageBatch? batch;
        try { batch = UsageBatch.Clean(JsonSerializer.Deserialize<UsageBatch>(request.Body, AgentResponse.Json), DateTimeOffset.Now); }
        catch (JsonException) { return new HttpReply(400, "text/plain", "Not usage stats."); }
        if (batch == null) return new HttpReply(400, "text/plain", "Not usage stats.");

        var received = DateTimeOffset.Now;
        var sb = new StringBuilder();
        foreach (var e in batch.Events)
            sb.AppendLine(JsonSerializer.Serialize(new StoredEvent
            {
                Received = received, Id = batch.Id, Version = batch.Version, Os = batch.Os,
                Type = e.Type, At = e.At, Mm = e.Mm, Server = e.Server, Seconds = e.Seconds, Count = e.Count, Bytes = e.Bytes, Ok = e.Ok, Code = e.Code,
            }, Line));
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(FolderPath);
                File.AppendAllText(Path.Combine(FolderPath, $"usage-{received:yyyy-MM-dd}.jsonl"), sb.ToString());
            }
        }
        catch (Exception ex)
        {
            log("Usage stats: couldn't save: " + ex.Message);
            return new HttpReply(500, "text/plain", "Couldn't save.");
        }
        MaybeRefresh();
        return new HttpReply(200, "application/json", "{\"ok\":true}");
    }

    /// <summary>Rewrites summary.txt (at most every 10 minutes) and deletes day files past KeepUsageDays.</summary>
    public void MaybeRefresh(bool force = false)
    {
        var now = DateTime.Now;
        if (!force && now - _lastSummary < SummaryEvery) return;
        if (Interlocked.Exchange(ref _summaryRunning, 1) == 1) return;
        _lastSummary = now;
        _ = Task.Run(() =>
        {
            try
            {
                if (now - _lastPrune > TimeSpan.FromHours(6)) { _lastPrune = now; Prune(now); }
                var text = BuildSummary(ReadEvents(now.AddDays(-30)), DateTimeOffset.Now);
                lock (_fileLock) File.WriteAllText(Path.Combine(FolderPath, SummaryFile), text);
            }
            catch (Exception ex) { log("Usage stats: summary failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref _summaryRunning, 0); }
        });
    }

    private void Prune(DateTime now)
    {
        if (!Directory.Exists(FolderPath)) return;
        foreach (var f in Directory.EnumerateFiles(FolderPath, "usage-*.jsonl"))
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f)[6..], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
                && day < now.Date.AddDays(-config.KeepUsageDays))
                try { File.Delete(f); } catch { }
    }

    public static List<StoredEvent> ReadEvents(DateTime since)
    {
        var list = new List<StoredEvent>();
        if (!Directory.Exists(FolderPath)) return list;
        foreach (var f in Directory.EnumerateFiles(FolderPath, "usage-*.jsonl").Order())
        {
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f)[6..], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
                && day < since.Date) continue;
            foreach (var line in File.ReadLines(f))
            {
                try { if (JsonSerializer.Deserialize<StoredEvent>(line, AgentResponse.Json) is { } e) list.Add(e); }
                catch (JsonException) { }
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ summary.txt

    public static string BuildSummary(List<StoredEvent> all, DateTimeOffset now)
    {
        var week = all.Where(e => e.At >= now.AddDays(-7)).ToList();
        var month = all.Where(e => e.At >= now.AddDays(-30)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("MM Launcher usage stats");
        sb.AppendLine($"Made {now:yyyy-MM-dd HH:mm} by MM Server Agent. Rewritten every 10 minutes while stats come in.");
        sb.AppendLine("Anonymous: each launcher is a random ID; no names, Steam IDs or IP addresses are kept.");
        sb.AppendLine("Only players who said yes to usage stats are counted. Raw data: usage-YYYY-MM-DD.jsonl in this folder.");
        sb.AppendLine();
        sb.AppendLine($"{"",-44}{"Last 7 days",14}{"Last 30 days",14}");
        void Row(string label, Func<List<StoredEvent>, string> f) => sb.AppendLine($"{label,-44}{f(week),14}{f(month),14}");
        Row("Launchers active", l => l.Select(e => e.Id).Distinct().Count().ToString());
        Row("Launcher starts", l => l.Count(e => e.Type == "start").ToString());
        Row("Joins to MM servers", l => l.Count(e => e.Type == "join" && e.Mm == true).ToString());
        Row("Joins to other servers", l => l.Count(e => e.Type == "join" && e.Mm == false).ToString());
        Row("Launchers that joined only MM servers", l =>
        {
            var joined = l.Where(e => e.Type == "join").GroupBy(e => e.Id).ToList();
            return joined.Count == 0 ? "-" : $"{joined.Count(g => g.All(e => e.Mm == true))} of {joined.Count}";
        });
        Row("Hours played on MM servers", l => Hours(l.Where(e => e.Type == "play" && e.Mm == true)));
        Row("Hours played on other servers", l => Hours(l.Where(e => e.Type == "play" && e.Mm == false)));
        Row("Average session on MM servers", l => Avg(l.Where(e => e.Type == "play" && e.Mm == true)));
        Row("DayZ crashes (share of sessions)", l =>
        {
            var plays = l.Where(e => e.Type == "play").ToList();
            int crashes = plays.Count(e => e.Ok == false);
            return plays.Count == 0 ? "-" : $"{crashes} ({100.0 * crashes / plays.Count:0}%)";
        });
        Row("Mod downloads", l => l.Count(e => e.Type == "sync").ToString());
        Row("  downloaded", l => Size(l.Where(e => e.Type == "sync").Sum(e => e.Bytes ?? 0)));
        Row("  average time", l => Avg(l.Where(e => e.Type == "sync" && e.Ok == true)));
        Row("  didn't finish", l => l.Count(e => e.Type == "sync" && e.Ok == false).ToString());
        Row("DayZ Editor starts", l => l.Count(e => e.Type == "editor").ToString());

        sb.AppendLine();
        sb.AppendLine("MM servers, last 7 days");
        sb.AppendLine($"  {"Server",-52}{"Joins",8}{"Players",9}{"Hours",9}{"Avg",10}{"Crashes",9}");
        var byServer = week.Where(e => e.Mm == true && e.Server.Length > 0).GroupBy(e => e.Server).OrderByDescending(g => g.Count(e => e.Type == "join"));
        foreach (var g in byServer)
        {
            var plays = g.Where(e => e.Type == "play").ToList();
            sb.AppendLine($"  {Trim(g.Key, 50),-52}{g.Count(e => e.Type == "join"),8}{g.Select(e => e.Id).Distinct().Count(),9}{Hours(plays),9}{Avg(plays),10}{plays.Count(e => e.Ok == false),9}");
        }
        if (!byServer.Any()) sb.AppendLine("  (none yet)");

        sb.AppendLine();
        sb.AppendLine("Errors, last 7 days");
        var errors = week.Where(e => e.Type == "error").GroupBy(e => e.Code).OrderByDescending(g => g.Count()).ToList();
        foreach (var g in errors) sb.AppendLine($"  {g.Key,-30}{g.Count(),6}  ({g.Select(e => e.Id).Distinct().Count()} launcher(s))  {ExplainError(g.Key)}");
        if (errors.Count == 0) sb.AppendLine("  (none)");

        sb.AppendLine();
        sb.AppendLine("Launcher versions, last 7 days (each launcher's newest)");
        foreach (var g in week.GroupBy(e => e.Id).Select(g => g.OrderBy(e => e.At).Last().Version).GroupBy(v => v).OrderByDescending(g => g.Key))
            sb.AppendLine($"  {g.Key,-30}{g.Count(),6}");
        sb.AppendLine();
        sb.AppendLine("Windows, last 7 days");
        foreach (var g in week.GroupBy(e => e.Id).Select(g => g.Last().Os).GroupBy(o => o).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Key,-30}{g.Count(),6}");
        return sb.ToString();
    }

    private static string ExplainError(string code) => code switch
    {
        "dayz-not-started" => "DayZ didn't open within 45 s of JOIN",
        "launch-failed" => "the launcher couldn't start DayZ at all",
        "steam-is-admin" => "Steam was running as administrator (launcher warned)",
        "launcher-is-admin" => "the launcher was running as administrator (warned)",
        _ => "",
    };

    private static string Hours(IEnumerable<StoredEvent> plays) => $"{plays.Sum(e => e.Seconds ?? 0) / 3600.0:0.0}";
    private static string Avg(IEnumerable<StoredEvent> list)
    {
        var s = list.Where(e => e.Seconds != null).Select(e => e.Seconds!.Value).ToList();
        if (s.Count == 0) return "-";
        var t = TimeSpan.FromSeconds(s.Average());
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
    }
    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB";
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>One line of a usage-YYYY-MM-DD.jsonl file.</summary>
    public sealed class StoredEvent
    {
        public DateTimeOffset Received { get; set; }
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Os { get; set; } = "";
        public string Type { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public bool? Mm { get; set; }
        public string Server { get; set; } = "";
        public int? Seconds { get; set; }
        public int? Count { get; set; }
        public long? Bytes { get; set; }
        public bool? Ok { get; set; }
        public string Code { get; set; } = "";
    }
}
