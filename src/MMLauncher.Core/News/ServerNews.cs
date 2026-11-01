using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLauncher.Core.News;

/// <summary>One news post for a server (a wipe date, an event, new mods…).</summary>
public sealed class NewsItem
{
    /// <summary>When it was posted, if the admin wrote a date.</summary>
    public DateOnly? Date { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Optional https link (Discord post, patch notes…). "" = none.</summary>
    public string Link { get; set; } = "";
    /// <summary>Written with "#!": shown highlighted (wipes, downtime).</summary>
    public bool Important { get; set; }

    /// <summary>Identifies the post for "have I seen this?" (changes when the title or date does).</summary>
    public string Key => $"{Date:yyyy-MM-dd}|{Title}";
}

/// <summary>
/// Reads the agent's news files. Format (lines starting with // are comments):
/// <code>
/// # 2026-11-07 Deer Isle wipe Friday
/// Full wipe Friday at 6 PM Central. Bases, vehicles and trader stock reset.
/// link: https://discord.com/channels/...
///
/// #! Downtime tonight
/// The box is getting new hardware at 2 AM. Back by 4 AM.
/// </code>
/// "# " starts a post (an optional yyyy-mm-dd date, then the title); "#!" marks it important.
/// Everything until the next "#" line is the text. Newest first, the way they're written.
/// </summary>
public static partial class NewsFormat
{
    public const int MaxItems = 8;
    public const int MaxTitle = 100;
    public const int MaxBody = 1500;
    public const int MaxLink = 300;
    public const int MaxFileBytes = 64 * 1024;

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2})\s*(?:[-|:·]\s*)?(?<title>.*)$")]
    private static partial Regex DatedTitleRx();

    public static List<NewsItem> Parse(string text)
    {
        if (text.Length > MaxFileBytes) text = text[..MaxFileBytes];
        var items = new List<NewsItem>();
        NewsItem? current = null;
        var body = new StringBuilder();

        void Finish()
        {
            if (current == null) return;
            current.Body = CleanBody(body.ToString());
            if (current.Title.Length > 0 || current.Body.Length > 0) items.Add(current);
            current = null;
            body.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('#'))
            {
                Finish();
                if (items.Count >= MaxItems) break;
                bool important = line.StartsWith("#!", StringComparison.Ordinal);
                var head = line.TrimStart('#', '!').Trim();
                current = new NewsItem { Important = important };
                if (DatedTitleRx().Match(head) is { Success: true } m &&
                    DateOnly.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    current.Date = date;
                    head = m.Groups["title"].Value;
                }
                current.Title = CleanLine(head, MaxTitle);
                continue;
            }
            if (current == null) continue; // text before the first "#" heading
            if (current.Link.Length == 0 && line.Trim().StartsWith("link:", StringComparison.OrdinalIgnoreCase))
            {
                current.Link = SafeLink(line.Trim()[5..].Trim());
                continue;
            }
            body.Append(line).Append('\n');
        }
        Finish();
        return items.Take(MaxItems).ToList();
    }

    /// <summary>Everything a launcher receives goes through here: limits, clean text, https links only.</summary>
    public static List<NewsItem> Sanitize(IEnumerable<NewsItem>? items) =>
        (items ?? []).Take(MaxItems).Select(i => new NewsItem
        {
            Date = i.Date,
            Title = CleanLine(i.Title, MaxTitle),
            Body = CleanBody(i.Body),
            Link = SafeLink(i.Link),
            Important = i.Important,
        }).Where(i => i.Title.Length > 0 || i.Body.Length > 0).ToList();

    /// <summary>Only plain https links (no scripts, files, or odd schemes), and nothing with spaces.</summary>
    public static string SafeLink(string? link)
    {
        link = (link ?? "").Trim();
        if (link.Length is 0 or > MaxLink || link.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "";
        return Uri.TryCreate(link, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Contains('.')
               && string.IsNullOrEmpty(u.UserInfo) ? u.AbsoluteUri : "";
    }

    private static string CleanLine(string? s, int max)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c) && !IsBidi(c)).ToArray()).Trim();
        return t.Length > max ? t[..(max - 1)].TrimEnd() + "…" : t;
    }

    /// <summary>Keeps line breaks (at most one blank line in a row), drops other control characters.</summary>
    private static string CleanBody(string? s)
    {
        var lines = (s ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(l => new string(l.Where(c => (!char.IsControl(c) || c == '\t') && !IsBidi(c)).ToArray()).Replace('\t', ' ').TrimEnd());
        var sb = new StringBuilder();
        bool blank = false;
        foreach (var l in lines)
        {
            if (l.Trim().Length == 0) { blank = sb.Length > 0; continue; }
            if (blank) sb.Append('\n');
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(l);
            blank = false;
        }
        var t = sb.ToString().Trim();
        return t.Length > MaxBody ? t[..(MaxBody - 1)].TrimEnd() + "…" : t;
    }

    // Right-to-left overrides and similar can make text read differently from what it is.
    private static bool IsBidi(char c) => c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏';
}

/// <summary>
/// Which news posts this PC has already shown (news-seen.jsonc), so new ones can be marked NEW.
/// Only post keys (date + title) per server are kept.
/// </summary>
public sealed class NewsSeen
{
    public const int MaxServers = 200;

    /// <summary>Server ("host:queryport") → keys of the posts already seen, most recent first.</summary>
    public Dictionary<string, List<string>> Seen { get; set; } = new();

    private static NewsSeen? _current;
    public static NewsSeen Current => _current ??= Load();

    public static string FilePath => Path.Combine(Config.LauncherSettings.DataFolder, "news-seen.jsonc");

    public bool IsSeen(string server, NewsItem item) => Seen.TryGetValue(server, out var keys) && keys.Contains(item.Key);

    /// <summary>Remembers these posts as seen. Returns true if anything changed.</summary>
    public bool MarkSeen(string server, IEnumerable<NewsItem> items)
    {
        var keys = items.Select(i => i.Key).ToList();
        if (keys.Count == 0) return false;
        if (!Seen.TryGetValue(server, out var list)) Seen[server] = list = [];
        if (keys.All(list.Contains)) return false;
        // Current posts first, then older ones still remembered (so a post that comes back stays seen).
        Seen[server] = keys.Concat(list.Where(k => !keys.Contains(k))).Take(40).ToList();
        if (Seen.Count > MaxServers)
            foreach (var extra in Seen.Keys.Where(k => k != server).Take(Seen.Count - MaxServers).ToList()) Seen.Remove(extra);
        return true;
    }

    public static NewsSeen Load()
    {
        try
        {
            if (File.Exists(FilePath) &&
                System.Text.Json.JsonSerializer.Deserialize<NewsSeen>(File.ReadAllText(FilePath), new System.Text.Json.JsonSerializerOptions
                {
                    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true,
                }) is { } s)
                return s;
        }
        catch (Exception) { }
        return new NewsSeen();
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("// MM Launcher: server news posts you've already seen, so new ones can be marked NEW.");
            sb.AppendLine("// Seen: server address (host:query port) -> the posts' date|title. Safe to delete (all news shows as NEW again).");
            sb.Append(System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            sb.AppendLine();
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception) { }
    }
}
