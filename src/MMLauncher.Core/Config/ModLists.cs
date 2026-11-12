using System.Text;
using System.Text.Json;
using MMLauncher.Core.Editor;

namespace MMLauncher.Core.Config;

/// <summary>One imported mod list (a DayZ launcher preset, or a list of Workshop links / IDs).</summary>
public sealed class ModList
{
    public string Name { get; set; } = "";
    public DateTimeOffset Imported { get; set; } = DateTimeOffset.Now;
    /// <summary>The mods, in the list's load order.</summary>
    public List<PresetMod> Mods { get; set; } = [];
}

/// <summary>
/// Mod lists the player imported on the MODS page, stored as mod-lists.jsonc. Their mods count
/// as "used", so "Remove unused" never removes them; forgetting a list releases them.
/// </summary>
public sealed class ModLists
{
    public const int MaxLists = 50;

    public List<ModList> Lists { get; set; } = [];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(LauncherSettings.DataFolder, "mod-lists.jsonc");

    private static ModLists? _current;
    public static ModLists Current => _current ??= Load();

    public static ModLists Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<ModLists>(File.ReadAllText(FilePath), ReadOptions) is { } l)
            {
                l.Lists = (l.Lists ?? []).Where(x => x != null && x.Name.Length > 0).Take(MaxLists).ToList();
                foreach (var x in l.Lists)
                    x.Mods = (x.Mods ?? []).Where(m => m != null && m.Id != 0).Take(PresetFormat.MaxMods).ToList();
                return l;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new ModLists();
    }

    /// <summary>
    /// Adds a list (renamed "Name (2)" etc. if the name is taken by a different list). Importing the
    /// same list again returns the one already stored. Returns null if it holds no mods.
    /// </summary>
    public ModList? Add(string name, IEnumerable<PresetMod> mods)
    {
        var list = mods.Where(m => m.Id != 0).GroupBy(m => m.Id).Select(g => g.First()).Take(PresetFormat.MaxMods)
                       .Select(m => new PresetMod { Id = m.Id, Name = m.Name }).ToList();
        if (list.Count == 0) return null;
        name = PresetFormat.Clean(name, 80);
        if (name.Length == 0) name = "Imported list";
        var same = Lists.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && l.Mods.Select(m => m.Id).SequenceEqual(list.Select(m => m.Id)));
        if (same != null) return same;
        var unique = name;
        for (int i = 2; Lists.Any(l => l.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++) unique = $"{name} ({i})";
        var stored = new ModList { Name = unique, Mods = list };
        Lists.Add(stored);
        if (Lists.Count > MaxLists) Lists.RemoveAt(0);
        Save();
        return stored;
    }

    public bool Remove(string name)
    {
        bool removed = Lists.RemoveAll(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save();
        return removed;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(LauncherSettings.DataFolder);
            var sb = new StringBuilder();
            sb.AppendLine("// MM Launcher: mod lists you imported on the MODS page (Import list).");
            sb.AppendLine("// Their mods count as used, so \"Remove unused\" keeps them. Forget a list on the MODS page");
            sb.AppendLine("// (pick it in the filter, then \"Forget this list\").");
            sb.AppendLine("//   Name      the list's name (from the file, or the file name)");
            sb.AppendLine("//   Imported  when it was imported");
            sb.AppendLine("//   Mods      Workshop ID and name of each mod, in load order");
            sb.Append(JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            sb.AppendLine();
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
