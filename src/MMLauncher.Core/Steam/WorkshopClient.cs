using System.Collections.Concurrent;
using System.Text.Json;

namespace MMLauncher.Core.Steam;

/// <summary>What the Steam Workshop says about one item.</summary>
public sealed record WorkshopItem(
    ulong Id,
    bool Exists,
    string Title,
    long SizeBytes,
    DateTimeOffset? Updated,
    string? PreviewUrl)
{
    public string PageUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={Id}";
}

/// <summary>
/// Looks up workshop items with Steam's public GetPublishedFileDetails endpoint.
/// No API key or Steam login needed. Results are cached for a few minutes.
/// </summary>
public sealed class WorkshopClient(HttpClient http)
{
    private const string Endpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<ulong, (WorkshopItem Item, DateTime At)> _cache = new();

    /// <param name="fresh">true = ask Steam now instead of using answers from the last few minutes
    /// (used right before joining, so a mod updated a moment ago is noticed).</param>
    public async Task<IReadOnlyDictionary<ulong, WorkshopItem>> GetDetailsAsync(
        IEnumerable<ulong> ids, CancellationToken ct = default, bool fresh = false)
    {
        var result = new Dictionary<ulong, WorkshopItem>();
        var needed = new List<ulong>();
        foreach (var id in ids.Distinct())
        {
            if (!fresh && _cache.TryGetValue(id, out var c) && DateTime.UtcNow - c.At < CacheFor)
                result[id] = c.Item;
            else
                needed.Add(id);
        }

        foreach (var batch in needed.Chunk(100))
        {
            var form = new List<KeyValuePair<string, string>> { new("itemcount", batch.Length.ToString()) };
            for (int i = 0; i < batch.Length; i++)
                form.Add(new($"publishedfileids[{i}]", batch[i].ToString()));

            using var response = await http.PostAsync(Endpoint, new FormUrlEncodedContent(form), ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("response", out var resp) ||
                !resp.TryGetProperty("publishedfiledetails", out var list))
                continue;

            foreach (var el in list.EnumerateArray())
            {
                var item = ParseItem(el);
                if (item == null) continue;
                result[item.Id] = item;
                _cache[item.Id] = (item, DateTime.UtcNow);
            }
        }
        return result;
    }

    private static WorkshopItem? ParseItem(JsonElement el)
    {
        var id = ReadULong(el, "publishedfileid");
        if (id is null) return null;
        bool exists = ReadLong(el, "result") == 1;
        if (!exists)
            return new WorkshopItem(id.Value, false, "", 0, null, null);

        var updated = ReadLong(el, "time_updated");
        return new WorkshopItem(
            id.Value,
            true,
            el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            ReadLong(el, "file_size") ?? 0,
            updated is > 0 ? DateTimeOffset.FromUnixTimeSeconds(updated.Value) : null,
            el.TryGetProperty("preview_url", out var p) ? p.GetString() : null);
    }

    // Steam returns some numbers as strings ("file_size": "538560"), so accept both.
    private static long? ReadLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static ulong? ReadULong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetUInt64(out var n) => n,
            JsonValueKind.String when ulong.TryParse(v.GetString(), out var s) => s,
            _ => null,
        };
    }
}
