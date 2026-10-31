using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MMLauncher.Core.Agent;
using MMLauncher.Core.Diagnostics;

namespace MMServerAgent;

/// <summary>
/// Accepts problem reports from the launcher, checks them, saves them in the "reports" folder
/// and posts them to the staff Discord channel. Built to be safe on a public port: size limits,
/// per-address and daily caps, zip checks, a disk cap, and Discord posts that can't ping anyone.
/// </summary>
public sealed class ReportReceiver(AgentConfig config, Action<string> log)
{
    private const int MaxZipEntries = 64;
    private const long MaxUncompressedBytes = 200L * 1024 * 1024;
    private static readonly HttpClient Discord = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly ConcurrentDictionary<string, List<DateTime>> _perAddress = new();
    private readonly List<DateTime> _all = [];
    private readonly object _gate = new();

    public static string ReportsFolder => Path.Combine(AppContext.BaseDirectory, "reports");
    public bool DiscordConfigured =>
        config.DiscordWebhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        // plain http only for a local test receiver
        config.DiscordWebhookUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);

    public async Task<HttpReply> HandleAsync(HttpRequestInfo request)
    {
        if (!config.AcceptReports)
            return Reply(403, false, "This server doesn't accept reports.");

        // 1) Rate limits (checked before doing any work on the body).
        string who = request.RemoteAddress.ToString();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var mine = _perAddress.GetOrAdd(who, _ => []);
            mine.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            _all.RemoveAll(t => now - t > TimeSpan.FromDays(1));
            if (mine.Count >= config.MaxReportsPerHourPerAddress)
                return Reply(429, false, "You've sent several reports in the last hour. Please wait a bit, or save the report as a zip and post it in Discord.");
            if (_all.Count >= config.MaxReportsPerDay)
                return Reply(429, false, "The server has received a lot of reports today. Please save the report as a zip and post it in Discord.");
            mine.Add(now);
            _all.Add(now);
        }

        // 2) Is it really one of our report zips?
        if (request.Body.Length < 4 || request.Body[0] != 'P' || request.Body[1] != 'K')
            return Reply(415, false, "That isn't a report zip.");

        ReportMeta? meta;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(request.Body), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries) return Reply(400, false, "Report has too many files.");
            if (zip.Entries.Sum(e => e.Length) > MaxUncompressedBytes) return Reply(400, false, "Report unpacks too large.");
            var json = zip.GetEntry("report.json");
            if (json == null || json.Length > 256 * 1024) return Reply(400, false, "Report is missing its summary.");
            using var reader = new StreamReader(json.Open());
            meta = JsonSerializer.Deserialize<ReportMeta>(reader.ReadToEnd(), ReportMeta.Json);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return Reply(400, false, "The report file is damaged: " + ex.Message);
        }
        if (meta == null) return Reply(400, false, "Report is missing its summary.");

        // 3) Save it.
        var reference = $"R{DateTime.Now:yyyyMMdd-HHmm}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2))}";
        var fileName = $"{reference}_{SafeName(meta.Player, "player")}_{SafeName(meta.ServerName, "server")}.zip";
        try
        {
            Directory.CreateDirectory(ReportsFolder);
            await File.WriteAllBytesAsync(Path.Combine(ReportsFolder, fileName), request.Body);
            TrimStorage();
        }
        catch (Exception ex)
        {
            log($"Report {reference}: couldn't save ({ex.Message})");
        }
        log($"Report {reference} from {MaskAddress(who)}: player '{Clip(meta.Player, 60)}', server '{Clip(meta.ServerName, 80)}', {request.Body.Length / 1024} KB");

        // 4) Discord.
        if (!DiscordConfigured)
            return Reply(200, true, $"Report received (reference {reference}). Staff will find it on the server.", reference);

        var (ok, error) = await PostToDiscordAsync(meta, request.Body, fileName, reference, who);
        if (ok) return Reply(200, true, $"Sent to staff (reference {reference}).", reference);
        log($"Report {reference}: Discord post failed ({error}); kept on disk.");
        return Reply(200, true, $"Report received (reference {reference}). It's saved on the server; posting to Discord failed, so mention the reference to staff.", reference);
    }

    private async Task<(bool Ok, string Error)> PostToDiscordAsync(ReportMeta m, byte[] zip, string fileName, string reference, string who)
    {
        var fields = new JsonArray
        {
            Field("Player", m.Player.Length > 0 ? m.Player : "(not given)", true),
            Field("Server", m.ServerName.Length > 0 ? m.ServerName : "(none selected)", true),
            Field("Crash", m.CrashSummary.Length > 0 ? m.CrashSummary : "No script crash recorded", false),
        };
        if (m.ErrorCodes.Count > 0) fields.Add(Field("Errors shown in game", string.Join("\n", m.ErrorCodes), false));
        fields.Add(Field("PC", $"{m.Windows}\n{m.Cpu}\n{m.Gpu}\n{m.Ram}", false));
        fields.Add(Field("DayZ session", m.SessionStarted is { } s ? s.ToString("yyyy-MM-dd HH:mm") : "no logs found", true));
        fields.Add(Field("Reference", $"{reference} · from {MaskAddress(who)}", true));

        var embed = new JsonObject
        {
            ["title"] = Clip($"Problem report: {(m.Player.Length > 0 ? m.Player : "unnamed player")}", 250),
            ["description"] = Clip(m.Description.Length > 0 ? m.Description : "_No description given._", 3500),
            ["color"] = 0xFF7A00,
            ["fields"] = fields,
            ["footer"] = new JsonObject { ["text"] = $"MM Launcher {Clip(m.LauncherVersion, 20)} · {m.Files.Count} files" },
            ["timestamp"] = m.Created.ToUniversalTime().ToString("o"),
        };
        var payload = new JsonObject
        {
            ["username"] = "MM Launcher",
            ["embeds"] = new JsonArray { embed },
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() }, // never ping anyone
        };

        async Task<HttpResponseMessage> Send(bool withFile)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), "payload_json");
            if (withFile)
            {
                var file = new ByteArrayContent(zip);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                form.Add(file, "files[0]", fileName);
            }
            return await Discord.PostAsync(config.DiscordWebhookUrl, form);
        }

        try
        {
            using var r = await Send(withFile: true);
            if (r.IsSuccessStatusCode) return (true, "");
            if (r.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                fields.Add(Field("Files", $"Too big for Discord; saved on the server as reports\\{fileName}", false));
                using var r2 = await Send(withFile: false);
                if (r2.IsSuccessStatusCode) return (true, "");
                return (false, $"{(int)r2.StatusCode} {Clip(await r2.Content.ReadAsStringAsync(), 200)}");
            }
            return (false, $"{(int)r.StatusCode} {Clip(await r.Content.ReadAsStringAsync(), 200)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Deletes the oldest saved reports once the folder is over its size budget.</summary>
    private void TrimStorage()
    {
        var files = new DirectoryInfo(ReportsFolder).EnumerateFiles("*.zip").OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length), budget = (long)config.KeepReportsMB * 1024 * 1024;
        foreach (var f in files)
        {
            if (total <= budget) break;
            total -= f.Length;
            try { f.Delete(); } catch { }
        }
    }

    private static JsonObject Field(string name, string value, bool inline) => new()
    {
        ["name"] = name,
        ["value"] = Clip(string.IsNullOrWhiteSpace(value) ? "-" : value, 1000),
        ["inline"] = inline,
    };

    private static HttpReply Reply(int status, bool ok, string message, string reference = "") =>
        new(status, "application/json", JsonSerializer.Serialize(new ReportUploadResult { Ok = ok, Message = message, Reference = reference }, AgentResponse.Json));

    private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string SafeName(string? s, string fallback)
    {
        var clean = new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray());
        return clean.Length > 0 ? clean : fallback;
    }

    /// <summary>Shows only part of the sender's address in logs and Discord ("203.0.113.x").</summary>
    private static string MaskAddress(string ip)
    {
        int dot = ip.LastIndexOf('.');
        return dot > 0 ? ip[..dot] + ".x" : ip.Length > 8 ? ip[..8] + "…" : ip;
    }
}
