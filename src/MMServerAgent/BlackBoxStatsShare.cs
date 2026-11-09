using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MMServerAgent;

/// <summary>
/// Lets the MM website's stats job download each DayZ server's MM BlackBox stats file
/// (&lt;profiles&gt;\MM_BlackBox\Stats\public_stats.json) for the leaderboards on
/// misfitmercenaries.com.
///
///   GET /mm-agent/v1/blackbox-stats/{game port}/{file}
///   Authorization: Bearer {BlackBoxStatsToken from agent.jsonc}
///
/// Locked down on purpose:
///  - off unless agent.jsonc has a BlackBoxStatsToken (at least 32 characters)
///  - encrypted connections only, so the password never crosses the internet in plain text
///  - only public_stats.json and public_stats_season_&lt;id&gt;.json, only from that one folder,
///    only for DayZ servers this agent has seen running; read-only
///  - 10 wrong passwords in an hour from one address blocks that address for the hour
/// </summary>
public sealed partial class BlackBoxStatsShare(AgentConfig config, Action<string> log)
{
    public const string PathPrefix = "/mm-agent/v1/blackbox-stats/";
    public const int MinTokenLength = 32;
    private const long MaxFileBytes = 25L * 1024 * 1024;
    private const int MaxFailuresPerHour = 10;
    private const int MaxTrackedAddresses = 1000;

    [GeneratedRegex(@"^public_stats(_season_[A-Za-z0-9_-]{1,40})?\.json$")]
    private static partial Regex AllowedFile();

    private readonly byte[] _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(config.BlackBoxStatsToken ?? ""));
    private readonly ConcurrentDictionary<int, string> _profiles = new();          // game port -> profiles folder
    private readonly ConcurrentDictionary<IPAddress, (int Count, DateTime Since)> _failures = new();

    public bool Enabled => (config.BlackBoxStatsToken ?? "").Length >= MinTokenLength;

    /// <summary>One line for the agent log at start-up.</summary>
    public string Describe() =>
        Enabled ? "Website stats share: on (MM BlackBox stats files, encrypted + password only)"
        : string.IsNullOrEmpty(config.BlackBoxStatsToken) ? "Website stats share: off"
        : $"Website stats share: OFF, BlackBoxStatsToken is shorter than {MinTokenLength} characters (run stats-token.bat to make a proper one)";

    /// <summary>Remembers each running server's profiles folder. Kept when a server restarts, so the stats stay available meanwhile.</summary>
    public void Remember(IReadOnlyDictionary<int, string>? profiles)
    {
        if (profiles == null) return;
        foreach (var (port, folder) in profiles) _profiles[port] = folder;
    }

    /// <summary>Where a server's stats files live, or null if this agent hasn't seen that server.</summary>
    public string? StatsFolder(int gamePort) =>
        _profiles.TryGetValue(gamePort, out var p) ? Path.GetFullPath(Path.Combine(p, "MM_BlackBox", "Stats")) : null;

    public HttpReply Handle(HttpRequestInfo request)
    {
        if (!Enabled) return new HttpReply(404, "text/plain", "Not found.");
        if (!request.Encrypted) return new HttpReply(403, "text/plain", "Encrypted connections only.");

        var address = request.RemoteAddress;
        if (_failures.TryGetValue(address, out var f) && f.Count >= MaxFailuresPerHour && DateTime.UtcNow - f.Since < TimeSpan.FromHours(1))
            return new HttpReply(429, "text/plain", "Too many wrong passwords. Try again later.");

        if (!PasswordMatches(request.Authorization))
        {
            Fail(address);
            log($"Website stats share: wrong or missing password from {address}");
            return new HttpReply(403, "text/plain", "Wrong password.");
        }
        _failures.TryRemove(address, out _);

        // {port}/{file}
        var parts = request.Path[PathPrefix.Length..].Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var port) || !AllowedFile().IsMatch(parts[1]))
            return new HttpReply(404, "text/plain", "Not found.");

        var folder = StatsFolder(port);
        if (folder == null) return new HttpReply(404, "text/plain", $"No DayZ server with game port {port} (or it has no -profiles= folder).");
        var file = Path.GetFullPath(Path.Combine(folder, parts[1]));
        if (!file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return new HttpReply(404, "text/plain", "Not found.");
        if (!File.Exists(file)) return new HttpReply(404, "text/plain", $"{parts[1]} hasn't been written yet.");

        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaxFileBytes) return new HttpReply(413, "text/plain", "Stats file is too large.");
            using var output = new MemoryStream();
            using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) input.CopyTo(gz);
            return new HttpReply(200, "application/json", "", output.ToArray(), "gzip");
        }
        catch (IOException ex)
        {
            // BlackBox may be replacing the file right now; the website keeps its last copy and tries again later.
            return new HttpReply(503, "text/plain", "Stats file is busy: " + ex.Message);
        }
    }

    private bool PasswordMatches(string authorization)
    {
        const string scheme = "Bearer ";
        var given = authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? authorization[scheme.Length..].Trim() : "";
        // Compare hashes in constant time, so the reply time gives nothing away.
        return given.Length > 0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), _tokenHash);
    }

    private void Fail(IPAddress address)
    {
        if (_failures.Count > MaxTrackedAddresses)
            foreach (var old in _failures.Where(kv => DateTime.UtcNow - kv.Value.Since > TimeSpan.FromHours(1)).ToList())
                _failures.TryRemove(old.Key, out _);
        _failures.AddOrUpdate(address, (1, DateTime.UtcNow),
            (_, v) => DateTime.UtcNow - v.Since > TimeSpan.FromHours(1) ? (1, DateTime.UtcNow) : (v.Count + 1, v.Since));
    }

    /// <summary>A new random password (43 characters, letters, digits, - and _).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
