using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MMLauncher.Core.Agent;
using MMLauncher.Core.Friends;

namespace MMServerAgent;

/// <summary>
/// "Which of my Steam friends are playing DayZ, and on which server?" for the launcher's
/// Friends tab. Uses the Steam Web API key in agent.jsonc (it never leaves this server):
/// GetFriendList for the player's friends, then GetPlayerSummaries for what they're playing.
/// Only works when the player's friends list is public, and only shows a friend's server
/// when that friend's game details are public (Steam's defaults).
/// Answers are cached for a minute per player, and each address is rate limited.
/// </summary>
public sealed class FriendsService(AgentConfig config, Action<string> log)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private const int MaxPerAddressPerMinute = 12;
    private const int MaxFriends = 2000;

    private readonly ConcurrentDictionary<ulong, (DateTime At, string Json)> _cache = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _hits = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public bool Enabled => config.SteamWebApiKey.Length > 0;

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("MMAGENT_STEAM_API") is { Length: > 0 } u ? u : "https://api.steampowered.com";

    public async Task<HttpReply> HandleAsync(string query, IPAddress remote)
    {
        if (!Enabled) return Reply(new FriendsReply { Message = "This MM server doesn't have the Friends feature turned on." });

        var steamIdText = System.Web.HttpUtility.ParseQueryString(query)["steamid"] ?? "";
        if (!ulong.TryParse(steamIdText, out var steamId) || !SteamAccount.IsValid(steamId))
            return Reply(new FriendsReply { Message = "That isn't a Steam ID." }, 400);

        // Cached answers are free; only fresh lookups count against the limit.
        if (_cache.TryGetValue(steamId, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            return new HttpReply(200, "application/json", hit.Json);

        if (!Admit(remote.ToString()))
            return Reply(new FriendsReply { Message = "Too many friend lookups from your address; try again in a minute." }, 429);

        FriendsReply reply;
        try
        {
            reply = await LookUpAsync(steamId);
        }
        catch (Exception ex)
        {
            log("Friends lookup failed: " + ex.Message);
            return Reply(new FriendsReply { Message = "Steam didn't answer. Try again in a minute." }, 503);
        }
        var json = JsonSerializer.Serialize(reply, AgentResponse.Json);
        if (_cache.Count > 5000) _cache.Clear();
        _cache[steamId] = (DateTime.UtcNow, json);
        return new HttpReply(200, "application/json", json);
    }

    private bool Admit(string who)
    {
        var now = DateTime.UtcNow;
        if (_hits.Count > 10_000) _hits.Clear();
        var list = _hits.GetOrAdd(who, _ => []);
        lock (list)
        {
            list.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (list.Count >= MaxPerAddressPerMinute) return false;
            list.Add(now);
            return true;
        }
    }

    private async Task<FriendsReply> LookUpAsync(ulong steamId)
    {
        var key = Uri.EscapeDataString(config.SteamWebApiKey);
        // 1) Friends. A private friends list answers 401.
        using var list = await _http.GetAsync($"{BaseUrl}/ISteamUser/GetFriendList/v1/?key={key}&steamid={steamId}&relationship=friend");
        if (list.StatusCode == HttpStatusCode.Unauthorized)
            return new FriendsReply
            {
                Message = "Your Steam friends list is private, so friends can't be shown. In Steam: your profile > Edit Profile > Privacy Settings > Friends List: Public.",
            };
        if (list.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Steam rejected the SteamWebApiKey in agent.jsonc.");
        if (!list.IsSuccessStatusCode) throw new InvalidOperationException($"Steam answered {(int)list.StatusCode}.");

        var ids = new List<string>();
        using (var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
            if (doc.RootElement.TryGetProperty("friendslist", out var fl) && fl.TryGetProperty("friends", out var friends))
                foreach (var f in friends.EnumerateArray())
                    if (f.TryGetProperty("steamid", out var sid) && sid.GetString() is { } s && ulong.TryParse(s, out var n) && SteamAccount.IsValid(n))
                        ids.Add(s);
        ids = ids.Take(MaxFriends).ToList();

        // 2) What each friend is doing, 100 at a time.
        var reply = new FriendsReply { Ok = true, FriendCount = ids.Count };
        foreach (var chunk in ids.Chunk(100))
        {
            using var res = await _http.GetAsync($"{BaseUrl}/ISteamUser/GetPlayerSummaries/v2/?key={key}&steamids={string.Join(",", chunk)}");
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Steam answered {(int)res.StatusCode}.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("players", out var players)) continue;
            foreach (var p in players.EnumerateArray())
            {
                if (!p.TryGetProperty("gameid", out var g) || g.GetString() != "221100") continue;
                reply.InDayZ.Add(new FriendInfo
                {
                    SteamId = p.TryGetProperty("steamid", out var s) ? s.GetString() ?? "" : "",
                    Name = Clip(p.TryGetProperty("personaname", out var pn) ? pn.GetString() : null, 64),
                    Server = p.TryGetProperty("gameserverip", out var ip) && ip.GetString() is { } addr && IsAddress(addr) ? addr : "",
                });
            }
        }
        return reply;
    }

    private static bool IsAddress(string s)
    {
        int colon = s.LastIndexOf(':');
        return colon > 0 && IPAddress.TryParse(s[..colon], out var ip) && !ip.Equals(IPAddress.Any) &&
               int.TryParse(s[(colon + 1)..], out var port) && port is > 0 and <= 65535;
    }

    private static string Clip(string? s, int max)
    {
        var clean = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max];
    }

    private static HttpReply Reply(FriendsReply r, int status = 200) =>
        new(status, "application/json", JsonSerializer.Serialize(r, AgentResponse.Json));
}
