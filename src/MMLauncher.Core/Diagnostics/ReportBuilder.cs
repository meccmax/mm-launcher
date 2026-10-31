using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MMLauncher.Core.Diagnostics;

/// <summary>One file that will go into a report.</summary>
public sealed record ReportFile(string ZipName, string SourcePath, long Size, string Description, bool IsCrashDump);

/// <summary>What a report would contain, worked out before anything is built, so the player can see it first.</summary>
public sealed class ReportPlan
{
    public string DayZLogFolder { get; init; } = "";
    public DateTime? SessionStarted { get; init; }
    public List<ReportFile> Files { get; init; } = [];
    public string CrashSummary { get; init; } = "";
    public List<string> ErrorCodes { get; init; } = [];
    public bool HasCrashDump => Files.Any(f => f.IsCrashDump);
}

/// <summary>
/// Builds a diagnostics zip: DayZ's logs from the most recent session, an optional crash dump,
/// the launcher log, and a short summary. Text files have the Windows username and PC name
/// removed. Nothing is sent anywhere by this class.
/// </summary>
public static partial class ReportBuilder
{
    /// <summary>Discord's upload limit for webhooks is 10 MB; stay safely under it.</summary>
    public const long DefaultMaxBytes = 9L * 1024 * 1024;
    private const long MaxTextBytes = 3L * 1024 * 1024;

    public static string DefaultDayZLogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZ");

    // ------------------------------------------------------------------ plan

    public static ReportPlan Plan(string dayzLogFolder, string? launcherLog)
    {
        var files = new List<ReportFile>();
        DateTime? session = null;
        string crash = "";
        var codes = new List<string>();

        if (Directory.Exists(dayzLogFolder))
        {
            var all = new DirectoryInfo(dayzLogFolder).EnumerateFiles().ToList();

            // The newest .RPT marks the most recent DayZ session; its log files share the timestamp.
            var rpt = all.Where(f => f.Name.StartsWith("DayZ_x64_", StringComparison.OrdinalIgnoreCase) &&
                                     f.Extension.Equals(".RPT", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (rpt != null)
            {
                session = StampOf(rpt.Name) ?? rpt.CreationTime;
                var stamp = StampText(rpt.Name);
                files.Add(new ReportFile($"dayz/{rpt.Name}", rpt.FullName, rpt.Length, "DayZ game log (RPT)", false));

                foreach (var prefix in new[] { "crash_", "script_", "error_", "warning_" })
                {
                    var f = all.Where(x => x.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && x.Extension == ".log")
                               .Where(x => stamp != null ? StampText(x.Name) == stamp : Math.Abs((x.LastWriteTime - rpt.LastWriteTime).TotalMinutes) < 30)
                               .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
                    if (f == null) continue;
                    files.Add(new ReportFile($"dayz/{f.Name}", f.FullName, f.Length, prefix switch
                    {
                        "crash_" => "Script crash log",
                        "script_" => "Script log",
                        "error_" => "Error log",
                        _ => "Warning log",
                    }, false));
                }

                // A crash dump only belongs to this session if it was written during it.
                var dump = all.Where(x => x.Extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase))
                              .Where(x => x.LastWriteTime >= (session ?? DateTime.MinValue).AddMinutes(-1))
                              .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
                if (dump != null)
                    files.Add(new ReportFile($"dayz/{dump.Name}", dump.FullName, dump.Length,
                        dump.Name.StartsWith("FROZEN", StringComparison.OrdinalIgnoreCase) ? "Crash dump (game froze)" : "Crash dump", true));

                try { crash = SummarizeCrashLog(files.FirstOrDefault(f => f.ZipName.Contains("/crash_"))?.SourcePath); } catch { }
                try { codes = FindErrorCodes(rpt.FullName); } catch { }
            }
        }

        if (launcherLog != null && File.Exists(launcherLog))
            files.Add(new ReportFile("launcher/launcher.log", launcherLog, new FileInfo(launcherLog).Length, "MM Launcher log", false));

        return new ReportPlan
        {
            DayZLogFolder = dayzLogFolder,
            SessionStarted = session,
            Files = files,
            CrashSummary = crash,
            ErrorCodes = codes,
        };
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Builds the zip. <paramref name="extraFiles"/> are generated text files (e.g. the server's
    /// mod list). If the result is over <paramref name="maxBytes"/>, the crash dump is dropped first,
    /// then logs are trimmed harder.
    /// </summary>
    public static byte[] Build(ReportPlan plan, ReportMeta meta, bool includeCrashDump,
        IReadOnlyDictionary<string, string> extraFiles, long maxBytes = DefaultMaxBytes)
    {
        foreach (var (textLimit, dump) in new[] { (MaxTextBytes, includeCrashDump), (MaxTextBytes, false), (MaxTextBytes / 3, false), (MaxTextBytes / 10, false) })
        {
            var bytes = BuildOnce(plan, meta, dump, extraFiles, textLimit);
            if (bytes.LongLength <= maxBytes) return bytes;
        }
        return BuildOnce(plan, meta, false, extraFiles, 64 * 1024);
    }

    private static byte[] BuildOnce(ReportPlan plan, ReportMeta meta, bool includeDump,
        IReadOnlyDictionary<string, string> extraFiles, long textLimit)
    {
        using var ms = new MemoryStream();
        var names = new List<string>();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in plan.Files)
            {
                if (f.IsCrashDump && !includeDump) continue;
                try
                {
                    var entry = zip.CreateEntry(f.ZipName, CompressionLevel.Optimal);
                    using var output = entry.Open();
                    if (f.IsCrashDump)
                    {
                        using var input = OpenShared(f.SourcePath);
                        input.CopyTo(output);
                    }
                    else
                    {
                        var text = Scrub(ReadTextLimited(f.SourcePath, textLimit));
                        output.Write(Encoding.UTF8.GetBytes(text));
                    }
                    names.Add(f.ZipName);
                }
                catch (Exception ex)
                {
                    WriteText(zip, f.ZipName + ".unreadable.txt", $"Couldn't read {Path.GetFileName(f.SourcePath)}: {ex.Message}");
                }
            }

            foreach (var (name, text) in extraFiles)
            {
                WriteText(zip, name, Scrub(text));
                names.Add(name);
            }

            meta.Files = names;
            meta.Player = Scrub(meta.Player);
            meta.Description = Scrub(meta.Description);
            WriteText(zip, "report.txt", Scrub(HumanSummary(meta, plan)));
            WriteText(zip, "report.json", JsonSerializer.Serialize(meta, ReportMeta.Json));
        }
        return ms.ToArray();
    }

    public static string HumanSummary(ReportMeta m, ReportPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MM Launcher problem report");
        sb.AppendLine("==========================");
        sb.AppendLine($"Created:      {m.Created:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Player:       {(m.Player.Length > 0 ? m.Player : "(not given)")}");
        sb.AppendLine($"Server:       {m.ServerName} ({m.ServerAddress}), {m.ModCount} mods");
        sb.AppendLine($"DayZ session: {(plan.SessionStarted is { } s ? s.ToString("yyyy-MM-dd HH:mm:ss") : "no DayZ logs found")}");
        sb.AppendLine($"Crash:        {(m.CrashSummary.Length > 0 ? m.CrashSummary : "none recorded")}");
        if (m.ErrorCodes.Count > 0) sb.AppendLine($"Errors shown: {string.Join("; ", m.ErrorCodes)}");
        sb.AppendLine();
        sb.AppendLine($"Windows:      {m.Windows}");
        sb.AppendLine($"CPU:          {m.Cpu}");
        sb.AppendLine($"GPU:          {m.Gpu}");
        sb.AppendLine($"RAM:          {m.Ram}");
        sb.AppendLine($"Launcher:     {m.LauncherVersion}");
        sb.AppendLine();
        sb.AppendLine("What happened (player's words):");
        sb.AppendLine(m.Description.Length > 0 ? m.Description : "(nothing written)");
        return sb.ToString();
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Opens a file even while DayZ still has it open for writing.</summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Reads a text file; if it's too big, keeps the start and the (more useful) end.</summary>
    private static string ReadTextLimited(string path, long limit)
    {
        using var fs = OpenShared(path);
        if (fs.Length <= limit)
        {
            using var r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd();
        }
        long head = limit / 5, tail = limit - head;
        var headBuf = new byte[head];
        fs.ReadExactly(headBuf);
        fs.Seek(-tail, SeekOrigin.End);
        var tailBuf = new byte[tail];
        fs.ReadExactly(tailBuf);
        return Encoding.UTF8.GetString(headBuf) +
               $"\n\n... [{(fs.Length - limit) / 1024} KB cut from the middle to keep the report small] ...\n\n" +
               Encoding.UTF8.GetString(tailBuf);
    }

    // ------------------------------------------------------------------ privacy

    /// <summary>Removes the Windows username and PC name from text (paths, mostly).</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var user = Environment.UserName;
        var machine = Environment.MachineName;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(profile) && profile.Length > 3)
            text = text.Replace(profile, Path.Combine(Path.GetDirectoryName(profile) ?? "", "<user>"), StringComparison.OrdinalIgnoreCase);
        // Any other "\Users\<name>" style path, e.g. written with forward slashes.
        text = UsersPathRegex().Replace(text, m => m.Groups[1].Value + "<user>");
        if (user.Length >= 3)
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9]){Regex.Escape(user)}(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase);
        if (machine.Length >= 3)
            text = Regex.Replace(text, $@"(?<![A-Za-z0-9]){Regex.Escape(machine)}(?![A-Za-z0-9])", "<pc>", RegexOptions.IgnoreCase);
        return text;
    }

    [GeneratedRegex(@"([\\/]Users[\\/])(?!<user>)[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex UsersPathRegex();

    // ------------------------------------------------------------------ log reading

    /// <summary>"crash_2026-10-30_17-54-58.log" -> the time 2026-10-30 17:54:58.</summary>
    public static DateTime? StampOf(string fileName)
    {
        var t = StampText(fileName);
        return t != null && DateTime.TryParseExact(t, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static string? StampText(string fileName) =>
        StampRegex().Match(fileName) is { Success: true } m ? m.Value : null;

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}")]
    private static partial Regex StampRegex();

    /// <summary>
    /// Turns the first entry of a DayZ crash_*.log into one line, e.g.
    /// "NULL pointer to instance · ExpansionP2PTraderAIIrena.eAIBase · AirborneAI/scripts/4_World/entity.c:13 (+1 more)".
    /// </summary>
    public static string SummarizeCrashLog(string? path)
    {
        if (path == null || !File.Exists(path)) return "";
        string text;
        using (var r = new StreamReader(OpenShared(path))) text = r.ReadToEnd();

        var blocks = text.Split("Virtual Machine Exception", StringSplitOptions.None).Skip(1).ToList();
        if (blocks.Count == 0) return "";
        var b = blocks[0];
        string Field(string name) =>
            Regex.Match(b, $@"^{name}:\s*'?([^'\r\n]+)'?", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        var reason = Field("Reason");
        var cls = Field("Class");
        var fn = Field("Function");
        var where = Regex.Match(b, @"Stack trace:\s*\r?\n\s*([^\r\n]+)") is { Success: true } s ? s.Groups[1].Value.Trim() : "";
        where = Regex.Replace(where, @"\s+Function\s+.*$", "");

        var parts = new List<string>();
        if (reason.Length > 0) parts.Add(reason);
        if (cls.Length > 0 || fn.Length > 0) parts.Add(cls.Length > 0 && fn.Length > 0 ? $"{cls}.{fn}" : cls + fn);
        if (where.Length > 0) parts.Add(where);
        var line = string.Join(" · ", parts);
        if (blocks.Count > 1) line += $" (+{blocks.Count - 1} more)";
        return line;
    }

    /// <summary>Error codes DayZ showed on screen, from its RPT ("Error thrown: 0x000400F0 (...)").</summary>
    public static List<string> FindErrorCodes(string rptPath)
    {
        var found = new List<string>();
        using var r = new StreamReader(OpenShared(rptPath));
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            var m = ErrorCodeRegex().Match(line);
            if (!m.Success) continue;
            var code = m.Groups[1].Value + (m.Groups[2].Success ? " " + m.Groups[2].Value.Trim() : "");
            if (!found.Contains(code)) found.Add(code);
            if (found.Count >= 10) break;
        }
        return found;
    }

    [GeneratedRegex(@"Error thrown:\s*(0x[0-9A-Fa-f]+)\s*(\([^)]*\))?")]
    private static partial Regex ErrorCodeRegex();
}
