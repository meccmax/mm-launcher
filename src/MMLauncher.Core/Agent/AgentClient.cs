using System.Net.Http.Json;

namespace MMLauncher.Core.Agent;

/// <summary>An agent that answered, and where.</summary>
public sealed record AgentReply(string Host, int Port, AgentResponse Response);

/// <summary>
/// Asks MM Server Agents for the true server and mod lists.
/// Each server machine runs one agent on its own TCP port; we try every configured
/// port on every MM host at once and keep whatever answers.
/// </summary>
public sealed class AgentClient(HttpClient http)
{
    public const string Path = "/mm-agent/v1/servers";
    public const string ReportPath = "/mm-agent/v1/report";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<List<AgentReply>> QueryAllAsync(IEnumerable<string> hosts, IEnumerable<int> ports, CancellationToken ct = default)
    {
        var tasks = (from host in hosts.Distinct(StringComparer.OrdinalIgnoreCase)
                     from port in ports.Distinct()
                     select QueryAsync(host, port, ct)).ToList();
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).Select(r => r!).ToList();
    }

    /// <summary>Sends a report zip to an agent. Never throws; failures come back as Ok = false.</summary>
    public async Task<ReportUploadResult> UploadReportAsync(string host, int port, byte[] zip, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            using var content = new ByteArrayContent(zip);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            using var response = await http.PostAsync($"http://{host}:{port}{ReportPath}", content, cts.Token);
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
        catch (Exception ex)
        {
            return new ReportUploadResult { Ok = false, Message = "Couldn't reach the MM server agent: " + ex.Message };
        }
    }

    public async Task<AgentReply?> QueryAsync(string host, int port, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var response = await http.GetFromJsonAsync<AgentResponse>($"http://{host}:{port}{Path}", AgentResponse.Json, cts.Token);
            return response?.Agent == "MM Server Agent" ? new AgentReply(host, port, response) : null;
        }
        catch
        {
            return null; // no agent there; that's normal
        }
    }
}
