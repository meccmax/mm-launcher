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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log($"MM Server Agent {Version} listening on TCP port {_config.Port}, rescanning every {_config.RescanSeconds}s");
        _reports = new ReportReceiver(_config, Log);
        Log(!_config.AcceptReports ? "Problem reports: off"
            : _reports.DiscordConfigured ? "Problem reports: on, posting to Discord and saving to the reports folder"
            : "Problem reports: on, saving to the reports folder (no Discord webhook set)");
        Rescan();
        var http = new MiniHttpServer(_config.Port, (long)_config.MaxReportMB * 1024 * 1024, HandleAsync);
        var server = http.RunAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_config.RescanSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
            Rescan();
        }
        await server;
    }

    private void Rescan()
    {
        try
        {
            var before = _last.Servers.Select(s => $"{s.GamePort}:{s.Mods.Count}").ToList();
            var detection = ServerDetector.Detect(_config);
            _last = detection;
            _lastScan = DateTime.Now;
            _json = JsonSerializer.Serialize(new AgentResponse
            {
                Version = Version,
                Generated = DateTimeOffset.Now,
                Servers = detection.Servers,
                ReportsEnabled = _config.AcceptReports,
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
            return new HttpReply(404, "text/plain", "Not found.");
        }

        if (path.Equals(AgentClient.Path, StringComparison.OrdinalIgnoreCase))
            return new HttpReply(200, "application/json", _json);

        if (path == "/")
        {
            var sb = new StringBuilder();
            sb.AppendLine($"MM Server Agent {Version}");
            sb.AppendLine($"Last check: {_lastScan:yyyy-MM-dd HH:mm:ss}");
            foreach (var s in _last.Servers)
                sb.AppendLine($"  port {s.GamePort} / query {s.QueryPort}: {s.Name} ({s.Mods.Count} client mods)");
            if (_last.Servers.Count == 0) sb.AppendLine("  No DayZ servers running.");
            sb.AppendLine($"Launcher data: {AgentClient.Path}");
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
