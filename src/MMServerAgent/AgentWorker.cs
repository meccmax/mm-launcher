using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MMLauncher.Core.Agent;

namespace MMServerAgent;

/// <summary>Re-checks the running servers on a timer and serves the latest snapshot.</summary>
public sealed class AgentWorker : BackgroundService
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly AgentConfig _config = AgentConfig.Load();
    private volatile string _json = "{}";
    private volatile ServerDetector.Detection _last = new([], []);
    private DateTime _lastScan;
    private ReportReceiver? _reports;
    private ServerListFeed? _feed;
    private EditorPresetFolder? _presets;
    private NewsFolder? _news;
    private PlayerHistoryRecorder? _history;
    private UsageReceiver? _usage;
    private FriendsService? _friends;
    private WorkshopSearchService? _workshop;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log($"MM Server Agent {Version} listening on TCP port {_config.Port}, rescanning every {_config.RescanSeconds}s");
        _reports = new ReportReceiver(_config, Log);
        Log(!_config.AcceptReports ? "Problem reports: off"
            : _reports.DiscordConfigured ? "Problem reports: on, posting to Discord and saving to the reports folder"
            : "Problem reports: on, saving to the reports folder (no Discord webhook set)");
        _feed = new ServerListFeed(_config, Log);
        EditorPresetFolder.EnsureFolder();
        _presets = new EditorPresetFolder(Log);
        NewsFolder.EnsureFolder();
        _news = new NewsFolder(Log);
        _history = new PlayerHistoryRecorder(Log);
        _usage = new UsageReceiver(_config, Log);
        Log(_config.AcceptUsageStats ? "Usage stats: on (anonymous; saved in the usage folder, see usage\\summary.txt)" : "Usage stats: off");
        _friends = new FriendsService(_config, Log);
        _workshop = new WorkshopSearchService(_config, Log);
        var feedTask = _feed.RunAsync(stoppingToken);
        Rescan();
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null;
        try
        {
            certificate = TlsIdentity.LoadOrCreate();
            Log($"Encryption: on. Key fingerprint {TlsIdentity.Fingerprint(certificate)}" +
                (_config.AllowPlainHttp ? " (older launchers still answered unencrypted)" : " (encrypted connections only)"));
        }
        catch (Exception ex)
        {
            Log($"Encryption: OFF, couldn't load or create {TlsIdentity.FileName}: {ex.Message}");
        }
        var http = new MiniHttpServer(_config.Port, (long)_config.MaxReportMB * 1024 * 1024, HandleAsync, BeforeBody,
            certificate, allowPlain: _config.AllowPlainHttp || certificate == null);
        var server = http.RunAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_config.RescanSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
            Rescan();
            if (_history != null)
            {
                try { await _history.SampleIfDueAsync(_last.Servers); }
                catch (Exception ex) { Log("Player history: " + ex.Message); }
            }
        }
        await server;
        await feedTask;
    }

    private void Rescan()
    {
        try
        {
            var before = _last.Servers.Select(s => $"{s.GamePort}:{s.Mods.Count}").ToList();
            var detection = ServerDetector.Detect(_config);
            foreach (var server in detection.Servers)
            {
                if (_news != null) server.News = _news.For(server.GamePort);
                server.HasHistory = _history?.Has(server.GamePort) == true;
            }
            _last = detection;
            _lastScan = DateTime.Now;
            _json = JsonSerializer.Serialize(new AgentResponse
            {
                Version = Version,
                Generated = DateTimeOffset.Now,
                Servers = detection.Servers,
                ReportsEnabled = _config.AcceptReports,
                BrowserFeed = _feed?.Enabled == true,
                EditorPresets = _presets?.Count ?? 0,
                UsageStats = _usage?.Enabled == true,
            }, AgentResponse.Json);

            var after = detection.Servers.Select(s => $"{s.GamePort}:{s.Mods.Count}").ToList();
            if (!before.SequenceEqual(after))
                Log($"Servers: {(after.Count == 0 ? "none running" : string.Join(", ", detection.Servers.Select(s => $"port {s.GamePort} ({s.Mods.Count} mods)")))}");
            foreach (var w in detection.Warnings) Log("Warning: " + w);
        }
        catch (Exception ex)
        {
            Log("Scan failed: " + ex.Message);
        }
    }

    /// <summary>Checks a POST before its body is received: only report uploads, and only if allowed.</summary>
    private HttpReply? BeforeBody(string method, string path, System.Net.IPAddress remote, long length)
    {
        if (path.Equals(MMLauncher.Core.Usage.UsageBatch.Path, StringComparison.OrdinalIgnoreCase) && _usage != null)
            return _usage.Admit(remote, length);
        if (!path.Equals(AgentClient.ReportPath, StringComparison.OrdinalIgnoreCase) || _reports == null)
            return new HttpReply(404, "text/plain", "Not found.");
        return _reports.Admit(remote, length);
    }

    private async Task<HttpReply> HandleAsync(HttpRequestInfo request)
    {
        var path = request.Path;
        if (request.Method == "POST")
        {
            if (path.Equals(AgentClient.ReportPath, StringComparison.OrdinalIgnoreCase) && _reports != null)
            {
                try { return await _reports.HandleAsync(request); }
                catch (Exception ex)
                {
                    Log("Report handling failed: " + ex);
                    return new HttpReply(500, "application/json", "{\"ok\":false,\"message\":\"The server hit an error saving the report.\"}");
                }
            }
            if (path.Equals(MMLauncher.Core.Usage.UsageBatch.Path, StringComparison.OrdinalIgnoreCase) && _usage != null)
                return _usage.Handle(request);
            return new HttpReply(404, "text/plain", "Not found.");
        }

        if (path.Equals(AgentClient.Path, StringComparison.OrdinalIgnoreCase))
            return new HttpReply(200, "application/json", _json);

        if (path.Equals(MMLauncher.Core.Workshop.WorkshopSearchReply.Path, StringComparison.OrdinalIgnoreCase) && _workshop != null)
            return await _workshop.HandleAsync(request.Query, request.RemoteAddress);

        if (path.Equals(MMLauncher.Core.History.PlayerHistoryReply.Path, StringComparison.OrdinalIgnoreCase) && _history != null)
            return _history.Handle(request.Query);

        if (path.Equals(MMLauncher.Core.Friends.FriendsReply.Path, StringComparison.OrdinalIgnoreCase) && _friends != null)
            return await _friends.HandleAsync(request.Query, request.RemoteAddress);

        if (path.Equals(EditorPresetFolder.Path, StringComparison.OrdinalIgnoreCase))
            return new HttpReply(200, "application/json", _presets?.Json ?? "[]");

        if (path.Equals(MMLauncher.Core.Browser.BrowserFeed.Path, StringComparison.OrdinalIgnoreCase))
            return _feed?.Gzip is { } gz
                ? new HttpReply(200, "application/json", "", gz, "gzip")
                : new HttpReply(503, "text/plain", _feed?.Enabled == true ? "The server list isn't ready yet." : "This agent has no server browser feed.");

        if (path == "/")
        {
            var sb = new StringBuilder();
            sb.AppendLine($"MM Server Agent {Version}");
            sb.AppendLine($"Last check: {_lastScan:yyyy-MM-dd HH:mm:ss}");
            foreach (var s in _last.Servers)
                sb.AppendLine($"  port {s.GamePort} / query {s.QueryPort}: {s.Name} ({s.Mods.Count} client mods)");
            if (_last.Servers.Count == 0) sb.AppendLine("  No DayZ servers running.");
            sb.AppendLine($"Launcher data: {AgentClient.Path}");
            sb.AppendLine($"DayZ Editor presets: {_presets?.Count ?? 0}");
            sb.AppendLine($"Server browser feed: {(_feed?.Enabled != true ? "off" : $"{_feed.Count} servers")}");
            sb.AppendLine($"Problem reports: {(!_config.AcceptReports ? "off" : _reports?.DiscordConfigured == true ? "on (Discord)" : "on (saved on server only)")}");
            return new HttpReply(200, "text/plain", sb.ToString());
        }
        return new HttpReply(404, "text/plain", "Not found.");
    }

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        Console.WriteLine(line);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "agent.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { }
    }
}
