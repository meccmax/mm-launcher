using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MMLauncher.Core.Config;
using MMLauncher.Core.Logging;

namespace MMLauncher.Core.Agent;

/// <summary>
/// Decides whether an MM Server Agent's encryption key is the real one.
///
///  1. Published keys: trust/agents.jsonc in the GitHub repository lists each official
///     agent's key fingerprint. If an agent is listed there, only that key is accepted.
///     The list is downloaded over HTTPS from GitHub (the same source updates come from),
///     cached, and a copy is built into the launcher for the first start.
///  2. Remembered keys: an agent that isn't listed is trusted the first time it's seen,
///     and from then on must keep the same key (like SSH). Stored in agent-keys.jsonc.
///
/// Either way, once an agent has answered over HTTPS the launcher never talks to it
/// unencrypted again, so an attacker can't quietly downgrade the connection.
/// </summary>
public sealed class AgentTrust
{
    public const string PublishedFileName = "agents.jsonc";
    private const string RepoPath = "trust/" + PublishedFileName;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);

    private readonly string _folder;
    private readonly object _gate = new();
    private Dictionary<string, HashSet<string>> _published = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RememberedKey> _remembered = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _problems = new();
    private DateTime _lastPublishedFetch = DateTime.MinValue;

    public sealed class RememberedKey
    {
        public string Fingerprint { get; set; } = "";
        public DateTimeOffset FirstSeen { get; set; }
    }

    private sealed class PublishedFile
    {
        public List<PublishedAgent> Agents { get; set; } = [];
    }

    public sealed class PublishedAgent
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Fingerprint { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="folder">Where agent-keys.jsonc and the cached published list live (the launcher's data folder).</param>
    public AgentTrust(string? folder = null)
    {
        _folder = folder ?? LauncherSettings.DataFolder;
        LoadRemembered();
        LoadPublished();
    }

    private string RememberedPath => Path.Combine(_folder, "agent-keys.jsonc");
    private string PublishedCachePath => Path.Combine(_folder, "agents-published.jsonc");

    /// <summary>MMLAUNCHER_TRUST_URL overrides where the published list is downloaded from (testing).</summary>
    public static string PublishedUrl =>
        Environment.GetEnvironmentVariable("MMLAUNCHER_TRUST_URL") is { Length: > 0 } u
            ? u
            : $"https://raw.githubusercontent.com/{Updates.UpdateChecker.Repository}/main/{RepoPath}";

    private static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    // ------------------------------------------------------------------ checks

    /// <summary>True when this agent must be reached over HTTPS (listed, or seen with a key before).</summary>
    public bool RequiresTls(string host, int port)
    {
        var key = Key(host, port);
        lock (_gate) return _published.ContainsKey(key) || _remembered.ContainsKey(key);
    }

    /// <summary>
    /// Called during the TLS handshake. True = this key may be used for this agent.
    /// A key that's merely "not seen before" passes here and is remembered by <see cref="Remember"/>
    /// only after a real agent answer came back over it.
    /// </summary>
    public bool Accepts(string host, int port, string fingerprint)
    {
        var key = Key(host, port);
        lock (_gate)
        {
            if (_published.TryGetValue(key, out var allowed))
            {
                if (allowed.Contains(fingerprint)) return true;
                Problem($"The MM agent at {host}:{port} presented an encryption key that doesn't match the published one, so it was ignored. " +
                        "If this keeps happening, tell staff.", key + fingerprint);
                return false;
            }
            if (_remembered.TryGetValue(key, out var known))
            {
                if (known.Fingerprint == fingerprint) return true;
                Problem($"The MM agent at {host}:{port} has a different encryption key than last time, so it was ignored. " +
                        $"If staff reinstalled it, delete agent-keys.jsonc in the settings folder.", key + fingerprint);
                return false;
            }
            return true; // first contact; remembered once it proves to be an agent
        }
    }

    /// <summary>Remembers the key of an agent that just answered over HTTPS (no-op if already known or published).</summary>
    public void Remember(string host, int port, string fingerprint)
    {
        var key = Key(host, port);
        lock (_gate)
        {
            if (_published.ContainsKey(key) || _remembered.ContainsKey(key)) return;
            _remembered[key] = new RememberedKey { Fingerprint = fingerprint, FirstSeen = DateTimeOffset.Now };
            Log.Info($"Remembered the encryption key of the MM agent at {host}:{port} ({fingerprint}).");
            SaveRemembered();
        }
    }

    private readonly HashSet<string> _reported = [];
    private void Problem(string message, string dedupeKey)
    {
        if (!_reported.Add(dedupeKey)) return; // say it once per session
        Log.Warn(message);
        _problems.Enqueue(message);
    }

    /// <summary>Warnings since the last call, for the status line.</summary>
    public List<string> TakeProblems()
    {
        var list = new List<string>();
        while (_problems.TryDequeue(out var p)) list.Add(p);
        return list;
    }

    // ------------------------------------------------------------------ fingerprints

    /// <summary>SHA-256 of the certificate's public key, base64 without padding (43 characters).</summary>
    public static string Fingerprint(X509Certificate2 cert)
    {
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(SHA256.HashData(spki)).TrimEnd('=');
    }

    // ------------------------------------------------------------------ published list

    /// <summary>Downloads the published list from GitHub if it's been a while. Never throws.</summary>
    public async Task RefreshPublishedAsync(HttpClient http, CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastPublishedFetch < RefreshEvery) return;
        _lastPublishedFetch = DateTime.UtcNow;
        try
        {
            var url = PublishedUrl;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                Environment.GetEnvironmentVariable("MMLAUNCHER_TRUST_URL") is not { Length: > 0 })
                return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var res = await http.GetAsync(url, cts.Token);
            if (!res.IsSuccessStatusCode) return;
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            if (text.Length > 256 * 1024 || ParsePublished(text) is not { } parsed) return;
            lock (_gate) _published = parsed;
            try { File.WriteAllText(PublishedCachePath, text); } catch { }
        }
        catch (Exception ex)
        {
            Log.Info("Couldn't refresh the published agent keys (using the saved copy): " + ex.Message);
        }
    }

    private void LoadPublished()
    {
        string? text = null;
        try { if (File.Exists(PublishedCachePath)) text = File.ReadAllText(PublishedCachePath); } catch { }
        if (text == null || ParsePublished(text) == null)
        {
            using var s = typeof(AgentTrust).Assembly.GetManifestResourceStream(PublishedFileName);
            if (s != null) text = new StreamReader(s).ReadToEnd();
        }
        if (text != null && ParsePublished(text) is { } parsed) _published = parsed;
    }

    /// <summary>Parses agents.jsonc; null if it's not valid.</summary>
    public static Dictionary<string, HashSet<string>>? ParsePublished(string text)
    {
        try
        {
            var file = JsonSerializer.Deserialize<PublishedFile>(text, ReadOptions);
            if (file == null) return null;
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in file.Agents)
            {
                if (string.IsNullOrWhiteSpace(a.Host) || a.Port is <= 0 or > 65535 || a.Fingerprint.Trim().Length != 43) continue;
                var key = Key(a.Host, a.Port);
                if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(a.Fingerprint.Trim());
            }
            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>For tests: replace the published list.</summary>
    public void SetPublished(string jsoncText)
    {
        lock (_gate) _published = ParsePublished(jsoncText) ?? throw new FormatException("bad published list");
    }

    // ------------------------------------------------------------------ remembered keys

    private void LoadRemembered()
    {
        try
        {
            if (!File.Exists(RememberedPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, RememberedKey>>(File.ReadAllText(RememberedPath), ReadOptions);
            if (map != null) _remembered = new Dictionary<string, RememberedKey>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("agent-keys.jsonc couldn't be read; starting fresh: " + ex.Message);
        }
    }

    private void SaveRemembered()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Encryption keys of MM Server Agents this launcher has talked to (\"host:port\": key).");
            sb.AppendLine("// The launcher checks that each agent keeps the same key, so nobody can pretend to be it.");
            sb.AppendLine("// Managed by the launcher. If staff reinstall an agent and the launcher warns about a");
            sb.AppendLine("// changed key, delete this file (or just that agent's entry) and it starts over.");
            sb.Append(JsonSerializer.Serialize(_remembered, new JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine();
            File.WriteAllText(RememberedPath, sb.ToString());
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't save agent-keys.jsonc: " + ex.Message);
        }
    }
}
