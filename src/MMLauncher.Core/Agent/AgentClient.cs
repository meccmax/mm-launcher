using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MMLauncher.Core.Logging;

namespace MMLauncher.Core.Agent;

/// <summary>An agent that answered, and where.</summary>
public sealed record AgentReply(string Host, int Port, AgentResponse Response, bool Encrypted = false);

/// <summary>
/// Asks MM Server Agents for the true server and mod lists, and sends them problem reports.
/// Each server machine runs one agent on its own TCP port; we try every configured
/// port on every MM host at once and keep whatever answers.
///
/// Agents speak HTTPS with their own key (checked by <see cref="AgentTrust"/>). Agents
/// older than v0.5 only speak plain HTTP; they're still used until they've been seen
/// with encryption once, after which plain HTTP is never used for them again.
/// </summary>
public sealed class AgentClient
{
    public const string Path = "/mm-agent/v1/servers";
    public const string ReportPath = "/mm-agent/v1/report";
    private const int MaxReplyBytes = 1024 * 1024;
    private const int MaxServers = 32;
    private const int MaxModsPerServer = 300;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly AgentTrust _trust;
    // Key each agent showed during its most recent handshake ("host:port" -> fingerprint).
    private readonly ConcurrentDictionary<string, string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public AgentTrust Trust => _trust;

    public AgentClient(AgentTrust trust, TimeSpan? overallTimeout = null)
    {
        _trust = trust;
        var handler = new HttpClientHandler
        {
            // Agents use their own key, not a public certificate, so the usual checks (trusted
            // authority, matching name) don't apply. The key itself is checked instead.
            ServerCertificateCustomValidationCallback = (request, cert, _, _) =>
            {
                if (cert == null || request.RequestUri is not { } uri) return false;
                var fp = AgentTrust.Fingerprint(cert);
                _seen[$"{uri.Host}:{uri.Port}"] = fp;
                return _trust.Accepts(uri.Host, uri.Port, fp);
            },
            UseProxy = false,
        };
        _http = new HttpClient(handler) { Timeout = overallTimeout ?? TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MMLauncher");
    }

    /// <summary>Clips everything an agent sends to sane sizes.</summary>
    public static AgentResponse Sanitize(AgentResponse r)
    {
        static string Clip(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var clean = new string(s.Where(c => !char.IsControl(c)).ToArray());
            return clean.Length <= max ? clean : clean[..max];
        }
        r.Version = Clip(r.Version, 20);
        r.Servers = r.Servers.Take(MaxServers).ToList();
        foreach (var s in r.Servers)
        {
            s.Name = Clip(s.Name, 200);
            s.DisplayName = s.DisplayName == null ? null : Clip(s.DisplayName, 120);
            s.Message = s.Message == null ? null : Clip(s.Message, 500);
            s.Map = Clip(s.Map, 40);
            s.Mods = s.Mods.Take(MaxModsPerServer).ToList();
            foreach (var m in s.Mods)
            {
                m.Name = Clip(m.Name, 200);
                m.Folder = Clip(m.Folder, 120);
            }
        }
        return r;
    }
    public async Task<List<AgentReply>> QueryAllAsync(IEnumerable<string> hosts, IEnumerable<int> ports, CancellationToken ct = default)
    {
        var tasks = (from host in hosts.Distinct(StringComparer.OrdinalIgnoreCase)
                     from port in ports.Distinct()
                     select QueryAsync(host, port, ct)).ToList();
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).Select(r => r!).ToList();
    }

    /// <summary>
    /// Asks one agent for its servers. Agents known to speak HTTPS are only asked over HTTPS.
    /// Unknown ones are asked both ways at once; an HTTPS answer wins, and its key is remembered.
    /// </summary>
    public async Task<AgentReply?> QueryAsync(string host, int port, CancellationToken ct = default)
    {
        if (_trust.RequiresTls(host, port))
            return await GetAsync(host, port, secure: true, ct);

        var secureTask = GetAsync(host, port, secure: true, ct);
        var plainTask = GetAsync(host, port, secure: false, ct);
        var secure = await secureTask;
        if (secure != null) return secure;
        // The HTTPS attempt failed; if that was because the key was refused, don't fall back.
        if (_trust.RequiresTls(host, port)) return null;
        return await plainTask;
    }

    private async Task<AgentReply?> GetAsync(string host, int port, bool secure, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            // Treat the reply as untrusted either way: bounded size, every field clipped.
            using var res = await _http.GetAsync($"{(secure ? "https" : "http")}://{host}:{port}{Path}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            if (res.Content.Headers.ContentLength > MaxReplyBytes) return null;
            await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[MaxReplyBytes + 1];
            int total = 0, n;
            while (total < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(total), cts.Token)) > 0) total += n;
            if (total > MaxReplyBytes) return null;
            var response = System.Text.Json.JsonSerializer.Deserialize<AgentResponse>(buffer.AsSpan(0, total), AgentResponse.Json);
            if (response?.Agent != "MM Server Agent") return null;
            if (secure && _seen.TryGetValue($"{host}:{port}", out var fp)) _trust.Remember(host, port, fp);
            return new AgentReply(host, port, Sanitize(response), secure);
        }
        catch
        {
            return null; // no agent there (or not this way); that's normal
        }
    }

    /// <summary>Sends a report zip to an agent. Never throws; failures come back as Ok = false.</summary>
    public async Task<ReportUploadResult> UploadReportAsync(string host, int port, byte[] zip, CancellationToken ct = default)
    {
        // Reports hold logs and PC details: encrypted whenever the agent supports it,
        // and never unencrypted to an agent that has used encryption before.
        bool secure = _trust.RequiresTls(host, port);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            using var content = new ByteArrayContent(zip);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            using var response = await _http.PostAsync($"{(secure ? "https" : "http")}://{host}:{port}{ReportPath}", content, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            try
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<ReportUploadResult>(body, AgentResponse.Json);
                if (result != null) return result;
            }
            catch (System.Text.Json.JsonException) { }
            return new ReportUploadResult { Ok = false, Message = $"The server answered {(int)response.StatusCode}: {body}" };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReportUploadResult { Ok = false, Message = "The upload timed out. Save the report as a zip instead and post it in Discord." };
        }
        catch (HttpRequestException ex) when (secure && ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return new ReportUploadResult { Ok = false, Message = "The MM server agent's encryption key didn't match, so the report wasn't sent. Save it as a zip and post it in Discord." };
        }
        catch (Exception ex)
        {
            return new ReportUploadResult { Ok = false, Message = "Couldn't reach the MM server agent: " + ex.Message };
        }
    }
}
