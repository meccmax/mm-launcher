using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MMLauncher.Core.Agent;
using MMLauncher.Core.Workshop;

namespace MMServerAgent;

/// <summary>
/// Steam Workshop search for the launcher's Mods page (IPublishedFileService/QueryFiles needs
/// a Steam Web API key, which stays here). DayZ items only. Results cached 5 minutes per
/// search; fresh searches rate limited per address.
/// </summary>
public sealed class WorkshopSearchService(AgentConfig config, Action<string> log)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private const int MaxPerAddressPerMinute = 30;
    private readonly ConcurrentDictionary<string, (DateTime At, string Json)> _cache = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _hits = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("MMAGENT_STEAM_API") is { Length: > 0 } u ? u : "https://api.steampowered.com";

    public async Task<HttpReply> HandleAsync(string query, IPAddress remote)
    {
        if (config.SteamWebApiKey.Length == 0)
            return Reply(new WorkshopSearchReply { Message = "This MM server doesn't have Workshop search turned on." });

        var q = System.Web.HttpUtility.ParseQueryString(query);
        var text = new string((q["q"] ?? "").Where(c => !char.IsControl(c)).Take(100).ToArray()).Trim();
        var sort = WorkshopSearchReply.Sorts.Any(s => s.Key == q["sort"]) ? q["sort"]! : "popular";
        var page = int.TryParse(q["page"], out var p) ? Math.Clamp(p, 1, 50) : 1;
        var cacheKey = $"{sort}|{page}|{text.ToLowerInvariant()}";

        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            return new HttpReply(200, "application/json", hit.Json);
        if (!Admit(remote.ToString()))
            return Reply(new WorkshopSearchReply { Message = "Too many searches from your address; wait a minute." }, 429);

        try
        {
            var reply = await SearchAsync(text, sort, page);
            var json = JsonSerializer.Serialize(reply, AgentResponse.Json);
            if (_cache.Count > 2000) _cache.Clear();
            _cache[cacheKey] = (DateTime.UtcNow, json);
            return new HttpReply(200, "application/json", json);
        }
        catch (Exception ex)
        {
            log("Workshop search failed: " + ex.Message);
            return Reply(new WorkshopSearchReply { Message = "Steam didn't answer the search. Try again in a minute." }, 503);
        }
    }

    private async Task<WorkshopSearchReply> SearchAsync(string text, string sort, int page)
    {
        // Steam query types: 12 text search, 3 trend (with days), 9 most subscribed, 21 last updated, 1 newest.
        int queryType = text.Length > 0 ? 12 : sort switch { "subscribed" => 9, "updated" => 21, "newest" => 1, _ => 3 };
        var url = $"{BaseUrl}/IPublishedFileService/QueryFiles/v1/?key={Uri.EscapeDataString(config.SteamWebApiKey)}" +
                  $"&appid=221100&query_type={queryType}&numperpage={WorkshopSearchReply.PerPage}&page={page}" +
                  "&return_short_description=true&return_children=true&strip_description_bbcode=true&days=7" +
                  (text.Length > 0 ? $"&search_text={Uri.EscapeDataString(text)}" : "");
        using var res = await _http.GetAsync(url);
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Steam rejected the SteamWebApiKey in agent.jsonc.");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Steam answered {(int)res.StatusCode}.");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var reply = new WorkshopSearchReply { Ok = true };
        if (!doc.RootElement.TryGetProperty("response", out var r)) return reply;
        if (r.TryGetProperty("total", out var t) && t.TryGetInt64(out var total)) reply.Total = total;
        if (!r.TryGetProperty("publishedfiledetails", out var items)) return reply;
        foreach (var i in items.EnumerateArray())
        {
            if (!ulong.TryParse(Str(i, "publishedfileid"), out var id) || id == 0) continue;
            if (i.TryGetProperty("consumer_appid", out var app) && app.TryGetInt32(out var appId) && appId != 221100) continue;
            var result = new WorkshopResult
            {
                Id = id,
                Title = Clip(Str(i, "title"), 120),
                SizeBytes = long.TryParse(Str(i, "file_size"), out var size) ? size : 0,
                Updated = i.TryGetProperty("time_updated", out var tu) && tu.TryGetInt64(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null,
                Subscriptions = i.TryGetProperty("subscriptions", out var subs) && subs.TryGetInt64(out var s) ? s : 0,
                Description = Clip(Str(i, "short_description"), 300),
                PreviewUrl = WorkshopSearchReply.IsSafePreview(Str(i, "preview_url")) ? Str(i, "preview_url") : "",
            };
            if (i.TryGetProperty("children", out var children))
                foreach (var c in children.EnumerateArray())
                    if (ulong.TryParse(Str(c, "publishedfileid"), out var cid) && cid != 0 && result.Requires.Count < 50) result.Requires.Add(cid);
            reply.Results.Add(result);
        }
        return reply;
    }

    private bool Admit(string who)
    {
        var now = DateTime.UtcNow;
        if (_hits.Count > 10_000) _hits.Clear();
        var list = _hits.GetOrAdd(who, _ => []);
        lock (list)
        {
            list.RemoveAll(x => now - x > TimeSpan.FromMinutes(1));
            if (list.Count >= MaxPerAddressPerMinute) return false;
            list.Add(now);
            return true;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static string Clip(string s, int max)
    {
        var clean = new string(s.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Replace('\n', ' ').Trim();
        return clean.Length <= max ? clean : clean[..max];
    }

    private static HttpReply Reply(WorkshopSearchReply r, int status = 200) =>
        new(status, "application/json", JsonSerializer.Serialize(r, AgentResponse.Json));
}
