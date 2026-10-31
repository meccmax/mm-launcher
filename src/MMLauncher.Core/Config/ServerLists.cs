using System.Text;
using System.Text.Json;

namespace MMLauncher.Core.Config;

/// <summary>A server the player starred or played on.</summary>
public sealed class SavedServer
{
    public string Host { get; set; } = "";
    public int QueryPort { get; set; }
    public int? GamePort { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset? LastPlayed { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => $"{Host}:{QueryPort}";
}

/// <summary>
/// Favorites and recently played servers, stored as favorites.jsonc next to the settings.
/// </summary>
public sealed class ServerLists
{
    public const int MaxRecent = 25;

    public List<SavedServer> Favorites { get; set; } = [];
    public List<SavedServer> Recent { get; set; } = [];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(LauncherSettings.DataFolder, "favorites.jsonc");

    public static ServerLists Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<ServerLists>(File.ReadAllText(FilePath), ReadOptions) is { } lists)
            {
                lists.Favorites = lists.Favorites.Where(s => s.Host.Length > 0 && s.QueryPort is > 0 and <= 65535).ToList();
                lists.Recent = lists.Recent.Where(s => s.Host.Length > 0 && s.QueryPort is > 0 and <= 65535).Take(MaxRecent).ToList();
                return lists;
            }
        }
        catch (JsonException) { }
        return new ServerLists();
    }

    public bool IsFavorite(string host, int queryPort) =>
        Favorites.Any(f => f.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && f.QueryPort == queryPort);

    /// <summary>Adds or removes a favorite; returns true if it's now a favorite.</summary>
    public bool ToggleFavorite(string host, int queryPort, int? gamePort, string name)
    {
        var existing = Favorites.FindIndex(f => f.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && f.QueryPort == queryPort);
        if (existing >= 0) Favorites.RemoveAt(existing);
        else Favorites.Add(new SavedServer { Host = host, QueryPort = queryPort, GamePort = gamePort, Name = name });
        Save();
        return existing < 0;
    }

    /// <summary>Puts a server at the top of the recent list.</summary>
    public void AddRecent(string host, int queryPort, int? gamePort, string name)
    {
        Recent.RemoveAll(r => r.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && r.QueryPort == queryPort);
        Recent.Insert(0, new SavedServer { Host = host, QueryPort = queryPort, GamePort = gamePort, Name = name, LastPlayed = DateTimeOffset.Now });
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        Save();
    }

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("// MM Launcher: your favorite servers and the ones you played on recently.");
            sb.AppendLine("// Managed by the launcher (the star button and JOIN SERVER). Safe to edit or delete.");
            sb.AppendLine("//   Favorites  servers you starred");
            sb.AppendLine($"//   Recent     the last {MaxRecent} servers you joined, newest first");
            sb.AppendLine("// Each entry: Host (address), QueryPort, GamePort, Name (as last seen), LastPlayed.");
            sb.Append(JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            sb.AppendLine();
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch { }
    }
}
