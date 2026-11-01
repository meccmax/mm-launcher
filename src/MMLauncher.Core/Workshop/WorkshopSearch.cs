namespace MMLauncher.Core.Workshop;

/// <summary>One DayZ Workshop item in search results.</summary>
public sealed class WorkshopResult
{
    public ulong Id { get; set; }
    public string Title { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public long Subscriptions { get; set; }
    public string Description { get; set; } = "";
    public string PreviewUrl { get; set; } = "";
    /// <summary>Workshop items this one needs (its "Required items" on Steam).</summary>
    public List<ulong> Requires { get; set; } = [];
}

/// <summary>Answer from GET /mm-agent/v1/workshop?q=&amp;sort=&amp;page=</summary>
public sealed class WorkshopSearchReply
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public long Total { get; set; }
    public List<WorkshopResult> Results { get; set; } = [];

    public const string Path = "/mm-agent/v1/workshop";
    public const int PerPage = 30;

    /// <summary>Sort orders the launcher offers (Steam's query types).</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Sorts =
    [
        ("popular", "Popular this week"),
        ("subscribed", "Most subscribed"),
        ("updated", "Recently updated"),
        ("newest", "Newest"),
    ];

    /// <summary>Only Steam's own image hosts are loaded as thumbnails.</summary>
    public static bool IsSafePreview(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".akamaihd.net", StringComparison.OrdinalIgnoreCase));

    /// <summary>A pasted Workshop link (…/filedetails/?id=123) or a bare Workshop ID.</summary>
    public static bool TryParseItem(string text, out ulong id)
    {
        id = 0;
        text = text.Trim();
        if (text.Length is 0 or > 300) return false;
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"^(?:https?://)?steamcommunity\.com/(?:sharedfiles|workshop)/filedetails/\?(?:[^\s]*&)?id=(?<id>\d{6,20})(?:&\S*)?$|^@?(?<id>\d{6,20})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && ulong.TryParse(m.Groups["id"].Value, out id) && id != 0;
    }
}
