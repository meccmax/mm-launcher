using System.Net;
using System.Net.Sockets;
using MMLauncher.Core.Agent;
using MMLauncher.Core.Config;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Discovery;
using MMLauncher.Core.Query;
using MMLauncher.Core.Steam;

namespace MMLauncher.Core.Services;

/// <summary>Live state of one server after a refresh.</summary>
public sealed record ServerStatus
{
    public required string Host { get; init; }
    public required int QueryPort { get; init; }
    public IPAddress? Address { get; init; }
    public bool Online { get; init; }
    public A2sInfo? Info { get; init; }
    public int PingMs { get; init; }
    public DayZTags Tags { get; init; } = new();
    public DayZRules? Rules { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset Updated { get; init; } = DateTimeOffset.Now;

    /// <summary>What this server's MM Server Agent reports, if one answered.</summary>
    public AgentServerInfo? Agent { get; init; }
}

/// <summary>Every server any agent reported in one refresh, with the address it was reached on.</summary>
public sealed class AgentSnapshot
{
    public List<(string Host, IPAddress Address, AgentServerInfo Server)> Servers { get; } = [];
    public int AgentCount { get; set; }

    /// <summary>Agents that accept problem reports: where to reach them, and which servers they run.</summary>
    public List<(string Host, int Port, IPAddress Address, List<int> QueryPorts)> ReportAgents { get; } = [];

    /// <summary>Agents that pass on the list of all DayZ servers.</summary>
    public List<(string Host, int Port)> BrowserAgents { get; } = [];

    /// <summary>Agents that share DayZ Editor presets.</summary>
    public List<(string Host, int Port)> PresetAgents { get; } = [];

    /// <summary>The report-accepting agent for a server, or any report-accepting agent if none matches.</summary>
    public (string Host, int Port)? ReportAgentFor(IPAddress? address, int queryPort)
    {
        var exact = ReportAgents.FirstOrDefault(a => address != null && a.Address.Equals(address) && a.QueryPorts.Contains(queryPort));
        if (exact.Host != null) return (exact.Host, exact.Port);
        var any = ReportAgents.FirstOrDefault();
        return any.Host != null ? (any.Host, any.Port) : null;
    }

    public AgentServerInfo? Find(IPAddress? address, int queryPort) =>
        address == null ? null : Servers.FirstOrDefault(s => s.Address.Equals(address) && s.Server.QueryPort == queryPort).Server;
}

public enum ModState
{
    /// <summary>Downloaded and at least as new as the workshop version.</summary>
    Installed,
    /// <summary>Downloaded but the workshop has a newer version.</summary>
    UpdateAvailable,
    /// <summary>Not downloaded on this PC.</summary>
    Missing,
    /// <summary>The workshop item is gone (deleted, hidden or banned).</summary>
    Unavailable,
    /// <summary>Steam wasn't found, so we can't tell.</summary>
    Unknown,
    /// <summary>A local mod with no workshop ID (agent couldn't find one).</summary>
    NoWorkshopId,
}

public static class ModStatus
{
    public static ModState Evaluate(InstalledMod? local, WorkshopItem? workshop, bool steamFound, ulong id = 1)
    {
        if (id == 0) return ModState.NoWorkshopId;
        if (workshop is { Exists: false }) return ModState.Unavailable;
        if (!steamFound) return ModState.Unknown;
        if (local == null) return ModState.Missing;
        if (workshop?.Updated is { } remote && local.Updated is { } mine && remote > mine.AddMinutes(1))
            return ModState.UpdateAvailable;
        return ModState.Installed;
    }
}

/// <summary>
/// Glue between the settings, the scanner, the per-server queries and Steam lookups.
/// The window talks to this; this talks to everything else.
/// </summary>
public sealed class LauncherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public LauncherSettings Settings { get; private set; }
    public ServerCache Cache { get; private set; }
    public WorkshopClient Workshop { get; } = new(Http);
    /// <summary>Which agent encryption keys are genuine (published list + remembered keys).</summary>
    public AgentTrust AgentTrust { get; } = new();
    public AgentClient Agents { get; }

    /// <summary>Separate client for report uploads: a few MB can take a while on slow upload links.</summary>
    public AgentClient ReportUploads { get; }

    /// <summary>What the agents said on the last refresh.</summary>
    public AgentSnapshot LastAgents { get; private set; } = new();

    static LauncherService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MMLauncher/0.1");
    }

    public LauncherService()
    {
        Agents = new AgentClient(AgentTrust);
        ReportUploads = new AgentClient(AgentTrust, TimeSpan.FromMinutes(3));
        Settings = LauncherSettings.Load();
        Cache = ServerCache.Load();
    }

    public void ReloadSettings() => Settings = LauncherSettings.Load();

    public bool ScanIsDue =>
        Cache.Servers.Count == 0 ||
        (Settings.RescanEveryHours > 0 &&
         (Cache.LastScan is null || DateTimeOffset.Now - Cache.LastScan > TimeSpan.FromHours(Settings.RescanEveryHours)));

    /// <summary>Full port scan of every MM host. Replaces the cached server list.</summary>
    public async Task<List<DiscoveredServer>> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var found = await ServerScanner.ScanAsync(Settings.Hosts, Settings.ScanPortStart, Settings.ScanPortEnd, progress, ct);

        // Don't wipe a good list because of a bad scan (e.g. PC offline).
        if (found.Count > 0 || Cache.Servers.Count == 0)
        {
            Cache = new ServerCache
            {
                LastScan = DateTimeOffset.Now,
                Servers = found.Select(s => new CachedServer
                {
                    Host = s.Host,
                    QueryPort = s.QueryPort,
                    GamePort = s.Info.GamePort,
                    LastKnownName = s.Info.Name,
                }).ToList(),
            };
            Cache.Save();
        }
        return found;
    }

    /// <summary>Asks every MM Server Agent (each configured port on each host) what it runs.</summary>
    public async Task<AgentSnapshot> QueryAgentsAsync(CancellationToken ct = default)
    {
        var snapshot = new AgentSnapshot();
        if (Settings.AgentPorts.Count == 0) return snapshot;

        var hosts = Settings.Hosts.Concat(Cache.Servers.Select(s => s.Host)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await AgentTrust.RefreshPublishedAsync(Http, ct);
        var replies = await Agents.QueryAllAsync(hosts, Settings.AgentPorts, ct);
        var seenAgents = new HashSet<string>();
        foreach (var reply in replies)
        {
            var address = await ResolveAsync(reply.Host, ct);
            if (address == null) continue;
            // Two hostnames for the same machine: count that agent once.
            if (!seenAgents.Add($"{address}:{reply.Port}")) continue;
            foreach (var server in reply.Response.Servers)
                snapshot.Servers.Add((reply.Host, address, server));
            if (reply.Response.EditorPresets > 0)
                snapshot.PresetAgents.Add((reply.Host, reply.Port));
            if (reply.Response.BrowserFeed)
                snapshot.BrowserAgents.Add((reply.Host, reply.Port));
            if (reply.Response.ReportsEnabled)
                snapshot.ReportAgents.Add((reply.Host, reply.Port, address, reply.Response.Servers.Select(s => s.QueryPort).ToList()));
        }
        snapshot.AgentCount = seenAgents.Count;
        LastAgents = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Adds servers that an agent reports but the scan didn't find (e.g. started since the
    /// last scan). Returns true when the list changed.
    /// </summary>
    public async Task<bool> MergeAgentServersAsync(AgentSnapshot snapshot, CancellationToken ct = default)
    {
        bool changed = false;
        foreach (var (host, address, server) in snapshot.Servers)
        {
            bool known = false;
            foreach (var cached in Cache.Servers.Where(c => c.QueryPort == server.QueryPort))
            {
                if (Equals(await ResolveAsync(cached.Host, ct), address)) { known = true; break; }
            }
            if (known) continue;
            Cache.Servers.Add(new CachedServer
            {
                Host = host,
                QueryPort = server.QueryPort,
                GamePort = server.GamePort,
                LastKnownName = server.DisplayName ?? server.Name,
            });
            changed = true;
        }
        if (changed) Cache.Save();
        return changed;
    }

    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        try { return (await Dns.GetHostAddressesAsync(host, ct)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
        catch (SocketException) { return null; }
    }

    /// <summary>Asks one server for its info and mod list.</summary>
    public async Task<ServerStatus> RefreshAsync(CachedServer server, CancellationToken ct = default)
    {
        IPAddress? address = null;
        try
        {
            address = IPAddress.TryParse(server.Host, out var literal)
                ? literal
                : (await Dns.GetHostAddressesAsync(server.Host, ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);

            var ep = new IPEndPoint(address, server.QueryPort);
            var (info, ping) = await A2sClient.QueryInfoAsync(ep, ct: ct);

            DayZRules? rules = null;
            string? note = null;
            try
            {
                rules = DayZRulesParser.Parse(await A2sClient.QueryRulesAsync(ep, ct: ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                note = "Mod list unavailable: " + ex.Message;
            }

            return new ServerStatus
            {
                Host = server.Host,
                QueryPort = server.QueryPort,
                Address = address,
                Online = true,
                Info = info,
                PingMs = ping,
                Tags = DayZTags.Parse(info.Keywords),
                Rules = rules,
                Error = note,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ServerStatus
            {
                Host = server.Host,
                QueryPort = server.QueryPort,
                Address = address,
                Online = false,
                Error = ex is SocketException ? "Couldn't look up " + server.Host : ex.Message,
            };
        }
    }

    public LocalSteam? DetectSteam() => LocalSteam.Detect(Settings.SteamPathOverride, Settings.DayZPathOverride, Settings.WorkshopPathOverride);
}
