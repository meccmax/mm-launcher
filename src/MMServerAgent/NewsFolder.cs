using MMLauncher.Core.News;

namespace MMServerAgent;

/// <summary>
/// Server news for the launcher's NEWS tab, from text files in the "news" folder next to the
/// agent: all.txt for every server, and &lt;game port&gt;.txt (e.g. 2302.txt) for one server.
/// Changes are picked up on the next rescan; no restart needed.
/// </summary>
public sealed class NewsFolder(Action<string> log)
{
    public const string FolderName = "news";
    public const string AllFile = "all.txt";
    private const int MaxFiles = 30;

    private Dictionary<string, List<NewsItem>> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private string _signature = "";

    public static string FolderPath => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>Makes the folder and a commented all.txt explaining the format, the first time.</summary>
    public static void EnsureFolder()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var all = Path.Combine(FolderPath, AllFile);
            if (!File.Exists(all))
                File.WriteAllText(all, StarterText);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Couldn't create {FolderName}: {ex.Message}");
        }
    }

    public const string StarterText =
        "// MM Server Agent: server news, shown in MM Launcher's NEWS tab for each server.\r\n" +
        "//\r\n" +
        "// This file (all.txt) is shown on EVERY server. For one server only, make a file named\r\n" +
        "// after its game port in this folder, e.g. 2302.txt. That server shows its own posts\r\n" +
        "// first, then the ones in all.txt. Saved changes show up within a minute; no restart.\r\n" +
        "//\r\n" +
        "// How to write a post:\r\n" +
        "//   # 2026-11-07 Deer Isle wipe Friday     <- \"# \", an optional date (year-month-day), the title\r\n" +
        "//   Full wipe Friday at 6 PM Central.      <- the text: any number of lines\r\n" +
        "//   link: https://discord.gg/yourinvite    <- optional: a button that opens this https link\r\n" +
        "//\r\n" +
        "//   #! Downtime tonight 2-4 AM             <- \"#!\" instead of \"#\" highlights it (wipes, downtime)\r\n" +
        "//\r\n" +
        "// Put the newest post at the top. Players see the first 8 posts (titles up to 100\r\n" +
        "// characters, text up to 1500). Launchers mark posts they haven't seen yet as NEW.\r\n" +
        "// Lines starting with // (like these) are ignored. Delete a post to take it down.\r\n" +
        "// Example (remove the // to use it):\r\n" +
        "//\r\n" +
        "// # 2026-11-07 Welcome to Misfit Mercenaries\r\n" +
        "// Join our Discord for events and wipe announcements.\r\n" +
        "// link: https://discord.gg/yourinvite\r\n";

    /// <summary>The posts for the server on this game port (its own first, then all.txt), or null if none.</summary>
    public List<NewsItem>? For(int gamePort)
    {
        Refresh();
        var list = new List<NewsItem>();
        if (_byFile.TryGetValue($"{gamePort}.txt", out var own)) list.AddRange(own);
        if (_byFile.TryGetValue(AllFile, out var all)) list.AddRange(all);
        return list.Count == 0 ? null : list.Take(NewsFormat.MaxItems).ToList();
    }

    private void Refresh()
    {
        try
        {
            if (!Directory.Exists(FolderPath)) { _byFile = new(StringComparer.OrdinalIgnoreCase); _signature = ""; return; }
            var files = Directory.EnumerateFiles(FolderPath, "*.txt")
                .Where(f => Path.GetFileName(f) is var n && (n.Equals(AllFile, StringComparison.OrdinalIgnoreCase) ||
                                                              int.TryParse(Path.GetFileNameWithoutExtension(n), out var port) && port is > 0 and <= 65535))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
            var signature = string.Join("|", files.Select(f => $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f).Ticks}"));
            if (signature == _signature) return;
            _signature = signature;

            var byFile = new Dictionary<string, List<NewsItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (new FileInfo(f).Length > NewsFormat.MaxFileBytes) log($"News: {name} is over 64 KB; only the start is used.");
                string text;
                using (var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    var buffer = new char[NewsFormat.MaxFileBytes];
                    text = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
                }
                var items = NewsFormat.Parse(text);
                if (items.Count > 0) byFile[name] = items;
            }
            _byFile = byFile;
            log(byFile.Count == 0 ? "News: no posts" : "News: " + string.Join(", ", byFile.Select(kv => $"{kv.Key} ({kv.Value.Count} post{(kv.Value.Count == 1 ? "" : "s")})")));
        }
        catch (Exception ex)
        {
            log("News: couldn't read the news folder: " + ex.Message);
        }
    }
}
