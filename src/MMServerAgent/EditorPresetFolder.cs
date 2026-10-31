using System.Text.Json;
using MMLauncher.Core.Agent;
using MMLauncher.Core.Editor;

namespace MMServerAgent;

/// <summary>
/// MM DayZ Editor presets for admins: preset files dropped into the "editor-presets" folder
/// next to the agent (exported from the DayZ launcher as .html, or a .txt list of Workshop
/// IDs/links) are offered to every launcher in its DayZ Editor tab. Changes are picked up
/// without restarting the agent.
/// </summary>
public sealed class EditorPresetFolder(Action<string> log)
{
    public const string FolderName = "editor-presets";
    public const string Path = "/mm-agent/v1/editor-presets";
    private const int MaxFiles = 50;

    private string _json = "[]";
    private int _count;
    private string _signature = "";

    public static string FolderPath => System.IO.Path.Combine(AppContext.BaseDirectory, FolderName);
    public int Count { get { Refresh(); return _count; } }

    public string Json { get { Refresh(); return _json; } }

    /// <summary>Makes the folder (with a short how-to) the first time.</summary>
    public static void EnsureFolder()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var readme = System.IO.Path.Combine(FolderPath, "README.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme,
                    "MM DayZ Editor presets\r\n" +
                    "======================\r\n" +
                    "Put preset files here and every MM Launcher shows them in its DayZ Editor tab\r\n" +
                    "as \"MM presets\" (read-only; players can copy one to make their own).\r\n\r\n" +
                    "Accepted files:\r\n" +
                    "  .html  exported from the DayZ launcher (Mods > Presets > Export) or the MM Launcher\r\n" +
                    "  .txt   one Steam Workshop ID or link per line, in load order\r\n\r\n" +
                    "The file name is the preset name (an .html export's own name wins).\r\n" +
                    "CF, Dabs Framework and DayZ-Editor are always loaded first; no need to list them.\r\n" +
                    "Changes show up within a minute; no restart needed.\r\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Couldn't create {FolderName}: {ex.Message}");
        }
    }

    private void Refresh()
    {
        try
        {
            if (!Directory.Exists(FolderPath)) { _json = "[]"; _count = 0; return; }
            var files = Directory.EnumerateFiles(FolderPath)
                .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                            (f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !System.IO.Path.GetFileName(f).Equals("README.txt", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
            var signature = string.Join("|", files.Select(f => $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f).Ticks}"));
            if (signature == _signature) return;
            _signature = signature;

            var presets = new List<EditorPreset>();
            foreach (var f in files)
            {
                if (new FileInfo(f).Length > PresetFormat.MaxBytes) { log($"Editor preset {System.IO.Path.GetFileName(f)} is over 1 MB; skipped."); continue; }
                var p = PresetFormat.Parse(File.ReadAllText(f), System.IO.Path.GetFileNameWithoutExtension(f));
                if (p.Mods.Count == 0) { log($"Editor preset {System.IO.Path.GetFileName(f)} has no Workshop mods in it; skipped."); continue; }
                presets.Add(p);
            }
            _json = JsonSerializer.Serialize(presets, AgentResponse.Json);
            _count = presets.Count;
            log($"Editor presets: {presets.Count} ({string.Join(", ", presets.Select(p => $"{p.Name}: {p.Mods.Count} mods"))})");
        }
        catch (Exception ex)
        {
            log("Editor presets couldn't be read: " + ex.Message);
        }
    }
}
