using System.Text;
using System.Text.Json;

namespace MMLauncher.Core.Config;

/// <summary>
/// When each Workshop mod was last loaded into DayZ by this launcher (joining a server or
/// starting the DayZ Editor), stored as mod-usage.jsonc. The Mods page uses it to point out
/// mods that haven't been played with in a while, so they can be removed to free space.
/// Only mod IDs and dates are kept.
/// </summary>
public sealed class ModUsage
{
    /// <summary>A mod counts as "not played lately" after this many days without being loaded.</summary>
    public const int StaleDays = 30;

    /// <summary>When tracking started. Mods never loaded since count from here, so nothing looks old on day one.</summary>
    public DateTimeOffset Since { get; set; } = DateTimeOffset.Now;

    /// <summary>Workshop ID → when it was last loaded.</summary>
    public Dictionary<string, DateTimeOffset> LastUsed { get; set; } = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(LauncherSettings.DataFolder, "mod-usage.jsonc");

    private static ModUsage? _current;
    public static ModUsage Current => _current ??= Load();

    public static ModUsage Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<ModUsage>(File.ReadAllText(FilePath), ReadOptions) is { } u)
            {
                u.LastUsed ??= new();
                if (u.Since > DateTimeOffset.Now) u.Since = DateTimeOffset.Now;
                return u;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        var fresh = new ModUsage();
        fresh.Save();
        return fresh;
    }

    /// <summary>Last time the mod was loaded, or null if never (since tracking started).</summary>
    public DateTimeOffset? LastUsedOf(ulong id) => LastUsed.TryGetValue(id.ToString(), out var t) ? t : null;

    /// <summary>True when the mod hasn't been loaded in <see cref="StaleDays"/> days (counting from <see cref="Since"/> if never).</summary>
    public bool IsStale(ulong id, DateTimeOffset now) => now - (LastUsedOf(id) ?? Since) > TimeSpan.FromDays(StaleDays);

    public void MarkUsed(IEnumerable<ulong> ids, DateTimeOffset? when = null)
    {
        var t = when ?? DateTimeOffset.Now;
        bool changed = false;
        foreach (var id in ids.Where(i => i != 0))
        {
            LastUsed[id.ToString()] = t;
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>Forgets removed mods so the file doesn't grow forever.</summary>
    public void Forget(IEnumerable<ulong> ids)
    {
        bool changed = false;
        foreach (var id in ids) changed |= LastUsed.Remove(id.ToString());
        if (changed) Save();
    }

    /// <summary>"today", "3 days ago", "2 months ago"; null if never loaded.</summary>
    public static string? Describe(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } t) return null;
        var days = (int)Math.Floor((now - t).TotalDays);
        return days switch
        {
            < 1 => "today",
            1 => "yesterday",
            < 60 => $"{days} days ago",
            < 730 => $"{days / 30} months ago",
            _ => $"{days / 365} years ago",
        };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(LauncherSettings.DataFolder);
            var sb = new StringBuilder();
            sb.AppendLine("// MM Launcher: when each Workshop mod was last loaded into DayZ (joining a server or starting");
            sb.AppendLine("// the DayZ Editor), so the Mods page can point out ones you haven't played with in a while.");
            sb.AppendLine("//   Since     when tracking started; mods never loaded since then count from this date");
            sb.AppendLine("//   LastUsed  Workshop ID -> when it was last loaded");
            sb.AppendLine("// Safe to delete (tracking starts over, and nothing counts as old for 30 days).");
            sb.Append(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine();
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
