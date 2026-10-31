using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MMLauncher.Core.Config;

namespace MMLauncher.Core.Editor;

/// <summary>One mod in a preset, in load order.</summary>
public sealed class PresetMod
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A named list of mods to load with the DayZ Editor.</summary>
public sealed class EditorPreset : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new(nameof(Name)));
        }
    }

    public List<PresetMod> Mods { get; set; } = [];

    /// <summary>"12 mods · MM preset" for the preset list.</summary>
    [JsonIgnore] public string Subtitle => $"{Mods.Count} mod{(Mods.Count == 1 ? "" : "s")}" + (Shared ? "  ·  shared by MM" : "");

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tells the list to redraw the mod count after the list changed.</summary>
    public void Touch() => PropertyChanged?.Invoke(this, new(nameof(Subtitle)));

    /// <summary>True for presets shared by an MM server agent (read-only in the launcher).</summary>
    [JsonIgnore] public bool Shared { get; set; }

    /// <summary>Mods the preset listed that aren't on the Workshop (local mods); they can't be loaded.</summary>
    [JsonIgnore] public int SkippedLocal { get; set; }
}

/// <summary>
/// The DayZ Editor and what it needs. The Editor only works offline, and needs
/// Community Framework and Dabs Framework loaded before it.
/// </summary>
public static class DayZEditor
{
    public const ulong CommunityFramework = 1559212036;
    public const ulong DabsFramework = 2545327648;
    public const ulong Editor = 2250764298;

    public static readonly IReadOnlyList<PresetMod> Required =
    [
        new() { Id = CommunityFramework, Name = "CF" },
        new() { Id = DabsFramework, Name = "Dabs Framework" },
        new() { Id = Editor, Name = "DayZ-Editor" },
    ];

    /// <summary>Takes CF, Dabs Framework and DayZ-Editor out of a preset's own list (they're always loaded first anyway).</summary>
    public static EditorPreset Normalize(EditorPreset preset)
    {
        var ids = Required.Select(m => m.Id).ToHashSet();
        preset.Mods.RemoveAll(m => m.Id == 0 || ids.Contains(m.Id));
        return preset;
    }

    /// <summary>The full load order: the Editor's required mods first, then the preset's, without duplicates.</summary>
    public static List<PresetMod> LoadOrder(EditorPreset preset)
    {
        var list = new List<PresetMod>(Required);
        var seen = Required.Select(m => m.Id).ToHashSet();
        foreach (var m in preset.Mods)
            if (m.Id != 0 && seen.Add(m.Id)) list.Add(m);
        return list;
    }
}

/// <summary>
/// Reads and writes presets in the HTML format the official DayZ launcher (and Arma's)
/// exports ("Export preset to file"), and also accepts plain lists of Workshop IDs or links.
/// </summary>
public static partial class PresetFormat
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxMods = 500;

    [GeneratedRegex(@"<tr[^>]*data-type\s*=\s*""ModContainer""[^>]*>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowRx();

    [GeneratedRegex(@"<td[^>]*data-type\s*=\s*""DisplayName""[^>]*>(?<name>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NameRx();

    [GeneratedRegex(@"(?:steamcommunity\.com/(?:sharedfiles|workshop)/filedetails/\?(?:[^""'\s<]*&)?id=)(?<id>\d{6,20})", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRx();

    [GeneratedRegex(@"<strong>(?<name>[^<]{1,120})</strong>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRx();

    [GeneratedRegex(@"^\s*@?(?<id>\d{6,20})\s*$")]
    private static partial Regex IdLineRx();

    /// <summary>
    /// Parses an exported preset (HTML), or text with Workshop links or IDs (one per line,
    /// or separated by ; or ,). Never throws; returns an empty preset if nothing is found.
    /// </summary>
    public static EditorPreset Parse(string text, string fallbackName)
    {
        if (text.Length > MaxBytes) text = text[..MaxBytes];
        var preset = new EditorPreset { Name = Clean(fallbackName, 80) };
        var seen = new HashSet<ulong>();

        void Add(ulong id, string name)
        {
            if (id == 0 || preset.Mods.Count >= MaxMods || !seen.Add(id)) return;
            preset.Mods.Add(new PresetMod { Id = id, Name = Clean(name, 120) is { Length: > 0 } n ? n : id.ToString() });
        }

        var rows = RowRx().Matches(text);
        if (rows.Count > 0)
        {
            if (TitleRx().Match(text) is { Success: true } title && Clean(title.Groups["name"].Value, 80) is { Length: > 0 } t)
                preset.Name = t;
            foreach (Match row in rows)
            {
                var body = row.Groups["row"].Value;
                var name = NameRx().Match(body) is { Success: true } n ? n.Groups["name"].Value : "";
                if (LinkRx().Match(body) is { Success: true } link && ulong.TryParse(link.Groups["id"].Value, out var id))
                    Add(id, name);
                else
                    preset.SkippedLocal++; // a local mod (no Workshop link)
            }
            return DayZEditor.Normalize(preset);
        }

        // Not a launcher export: take Workshop links in order, else bare IDs.
        var links = LinkRx().Matches(text);
        if (links.Count > 0)
        {
            foreach (Match l in links)
                if (ulong.TryParse(l.Groups["id"].Value, out var id)) Add(id, "");
            return DayZEditor.Normalize(preset);
        }
        foreach (var part in text.Split(['\n', '\r', ';', ','], StringSplitOptions.RemoveEmptyEntries))
            if (IdLineRx().Match(part) is { Success: true } m && ulong.TryParse(m.Groups["id"].Value, out var id))
                Add(id, "");
        return DayZEditor.Normalize(preset);
    }

    /// <summary>Writes a preset in the DayZ launcher's HTML format (it can import it back).</summary>
    public static string ToHtml(EditorPreset preset)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<html>");
        sb.AppendLine("  <!--Created by MM Launcher. Import it in the MM Launcher's DayZ Editor tab, or in the DayZ launcher (Mods > Presets > Import).-->");
        sb.AppendLine("  <head>");
        sb.AppendLine("    <meta name=\"arma:Type\" content=\"list\" />");
        sb.AppendLine($"    <meta name=\"arma:PresetName\" content=\"{WebUtility.HtmlEncode(preset.Name)}\" />");
        sb.AppendLine("    <meta name=\"generator\" content=\"MM Launcher\" />");
        sb.AppendLine($"    <title>DayZ</title>");
        sb.AppendLine("  </head>");
        sb.AppendLine("  <body>");
        sb.AppendLine($"    <h1>DayZ Mods - <strong>{WebUtility.HtmlEncode(preset.Name)}</strong></h1>");
        sb.AppendLine("    <div class=\"mod-list\">");
        sb.AppendLine("      <table>");
        foreach (var m in preset.Mods)
        {
            var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={m.Id}";
            sb.AppendLine("        <tr data-type=\"ModContainer\">");
            sb.AppendLine($"          <td data-type=\"DisplayName\">{WebUtility.HtmlEncode(m.Name)}</td>");
            sb.AppendLine("          <td><span class=\"from-steam\">Steam</span></td>");
            sb.AppendLine($"          <td><a href=\"{url}\" data-type=\"Link\">{url}</a></td>");
            sb.AppendLine("        </tr>");
        }
        sb.AppendLine("      </table>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>Decodes HTML entities, drops tags and control characters, trims and clips.</summary>
    public static string Clean(string s, int max)
    {
        s = WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]*>", ""));
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length <= max ? s : s[..max];
    }
}

/// <summary>The player's own presets, stored as editor-presets.jsonc next to the settings.</summary>
public sealed class EditorPresetStore
{
    public List<EditorPreset> Presets { get; set; } = [];

    public static string FilePath => Path.Combine(LauncherSettings.DataFolder, "editor-presets.jsonc");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static EditorPresetStore Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<EditorPresetStore>(File.ReadAllText(FilePath), ReadOptions) is { } store)
            {
                store.Presets = store.Presets.Where(p => p.Name.Length > 0).Select(DayZEditor.Normalize).ToList();
                return store;
            }
        }
        catch (JsonException) { }
        return new EditorPresetStore();
    }

    public void Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// MM Launcher: your DayZ Editor presets (the DayZ Editor tab).");
        sb.AppendLine("// Each preset: Name, and Mods in load order (Id = Steam Workshop ID, Name as shown).");
        sb.AppendLine("// CF, Dabs Framework and DayZ-Editor are always loaded first and needn't be listed.");
        sb.AppendLine("// Managed by the launcher; safe to edit while it's closed.");
        sb.Append(JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        sb.AppendLine();
        File.WriteAllText(FilePath, sb.ToString());
    }
}
