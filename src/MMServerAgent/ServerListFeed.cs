using System.Net;
using MMLauncher.Core.Browser;

namespace MMServerAgent;

/// <summary>
/// Keeps a fresh copy of Valve's list of every DayZ server for the launcher's
/// "All servers" browser. Asks the Steam Web API every few minutes with the key in
/// agent.jsonc (the key stays on this server), and keeps the last good list
/// (also on disk, so a restart serves it straight away).
/// </summary>
public sealed class ServerListFeed(AgentConfig config, Action<string> log)
{
    public const string CacheFile = "server-list-cache.json.gz";
    private const int Limit = 20000;
    private volatile byte[]? _gzip;
    private int _count;

    public bool Enabled => config.SteamWebApiKey.Length > 0;

    /// <summary>The latest list, gzip-compressed JSON (null until the first fetch).</summary>
    public byte[]? Gzip => _gzip;
    public int Count => _count;

    /// <summary>MMAGENT_STEAM_API overrides Valve's address (testing).</summary>
    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("MMAGENT_STEAM_API") is { Length: > 0 } u ? u : "https://api.steampowered.com";

    private static string CachePath => Path.Combine(AppContext.BaseDirectory, CacheFile);

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            log("Server browser feed: off (no SteamWebApiKey in agent.jsonc)");
            return;
        }
        try
        {
            if (File.Exists(CachePath) && BrowserFeed.FromGzip(await File.ReadAllBytesAsync(CachePath, ct)) is { } cached)
            {
                _gzip = await File.ReadAllBytesAsync(CachePath, ct);
                _count = cached.Servers.Count;
            }
        }
        catch { }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MMServerAgent");
        var every = TimeSpan.FromMinutes(Math.Max(2, config.ServerListRefreshMinutes));
        bool firstOk = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var feed = await FetchAsync(http, ct);
                var gz = feed.ToGzip();
                _gzip = gz;
                _count = feed.Servers.Count;
                try { await File.WriteAllBytesAsync(CachePath, gz, ct); } catch { }
                if (firstOk) log($"Server browser feed: {feed.Servers.Count} DayZ servers from Steam ({gz.Length / 1024} KB), refreshing every {every.TotalMinutes:0} min");
                if (feed.Servers.Count >= Limit) log($"Warning: Steam returned the maximum of {Limit} servers; some may be missing.");
                firstOk = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log("Server browser feed: couldn't get the list from Steam: " + ex.Message);
                firstOk = true; // log the next success again
            }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task<BrowserFeed> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString(@"\appid\221100");
        var url = $"{BaseUrl}/IGameServersService/GetServerList/v1/?key={Uri.EscapeDataString(config.SteamWebApiKey)}&filter={filter}&limit={Limit}";
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        // Never put the URL (it holds the key) in a message.
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Steam rejected the SteamWebApiKey in agent.jsonc (check it at steamcommunity.com/dev/apikey).");
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Steam answered {(int)res.StatusCode} {res.ReasonPhrase}.");
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        return BrowserFeed.FromSteamReply(body);
    }
}
