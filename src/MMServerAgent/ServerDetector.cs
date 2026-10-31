using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using MMLauncher.Core.Agent;

namespace MMServerAgent;

/// <summary>A running DayZ server process: where its exe is and what it was started with.</summary>
public sealed record ServerProcess(int Pid, string ExePath, IReadOnlyList<string>? Args, DateTime? Started);

/// <summary>
/// Finds running DayZ server processes and works out, from their own startup line and
/// files, exactly what they run: name, ports, map and the client mod list.
/// </summary>
public static partial class ServerDetector
{
    /// <summary>Problems worth showing in "check" output (e.g. couldn't read a startup line).</summary>
    public sealed record Detection(List<AgentServerInfo> Servers, List<string> Warnings);

    public static Detection Detect(AgentConfig config)
    {
        var warnings = new List<string>();
        var servers = new List<AgentServerInfo>();
        var names = config.ProcessNames.Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
                                       .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<ServerProcess> processes = OperatingSystem.IsWindows() ? WindowsProcesses(names) : LinuxProcesses(names);
        foreach (var p in processes)
        {
            if (p.Args == null)
            {
                warnings.Add($"PID {p.Pid}: couldn't read its startup line. Run the agent as administrator / as the service.");
                continue;
            }
            try
            {
                servers.Add(Describe(p, config, warnings));
            }
            catch (Exception ex)
            {
                warnings.Add($"PID {p.Pid}: {ex.Message}");
            }
        }
        return new Detection(servers.OrderBy(s => s.GamePort).ToList(), warnings);
    }

    public static AgentServerInfo Describe(ServerProcess p, AgentConfig config, List<string> warnings)
    {
        var root = Path.GetDirectoryName(p.ExePath) ?? ".";
        var opts = ParseOptions(p.Args!);

        int gamePort = int.TryParse(opts.GetValueOrDefault("port"), out var gp) ? gp : 2302;
        var cfgPath = Resolve(root, opts.GetValueOrDefault("config") ?? "serverDZ.cfg");
        var cfg = File.Exists(cfgPath) ? ReadServerCfg(File.ReadAllText(cfgPath)) : new ServerCfg();
        if (!File.Exists(cfgPath))
            warnings.Add($"Port {gamePort}: config not found at {cfgPath}");

        var mods = new List<AgentMod>();
        foreach (var entry in SplitModList(opts.GetValueOrDefault("mod")))
        {
            var dir = Resolve(root, entry);
            var folder = Path.GetFileName(dir.TrimEnd('\\', '/'));
            var meta = ReadMeta(Path.Combine(dir, "meta.cpp"));
            ulong id = meta.Id;
            if (id == 0 && ulong.TryParse(folder.TrimStart('@'), out var numeric)) id = numeric;
            if (!Directory.Exists(dir))
                warnings.Add($"Port {gamePort}: mod folder missing: {entry}");
            else if (id == 0)
                warnings.Add($"Port {gamePort}: no workshop ID for {folder} (no publishedid in meta.cpp)");
            mods.Add(new AgentMod
            {
                Id = id,
                Name = !string.IsNullOrWhiteSpace(meta.Name) ? meta.Name! : folder.TrimStart('@'),
                Folder = folder,
            });
        }

        var extras = config.ExtrasFor(gamePort);
        var restart = MMLauncher.Core.Agent.RestartSchedule.Next(
            DateTimeOffset.Now,
            p.Started is { } started ? new DateTimeOffset(started) : null,
            extras?.RestartEveryHours ?? config.RestartEveryHours,
            extras?.RestartTimes ?? config.RestartTimes);
        return new AgentServerInfo
        {
            Name = cfg.Hostname?.Trim() is { Length: > 0 } h ? h : $"DayZ server on port {gamePort}",
            DisplayName = string.IsNullOrWhiteSpace(extras?.DisplayName) ? null : extras!.DisplayName.Trim(),
            Message = string.IsNullOrWhiteSpace(extras?.Message) ? null : extras!.Message,
            GamePort = gamePort,
            QueryPort = cfg.SteamQueryPort ?? 27016,
            MaxPlayers = cfg.MaxPlayers ?? 0,
            Map = cfg.Map ?? "",
            UptimeSeconds = p.Started is { } s ? (long)Math.Max(0, (DateTime.Now - s).TotalSeconds) : 0,
            NextRestart = restart?.At,
            RestartApproximate = restart?.Approximate ?? false,
            Mods = mods,
        };
    }

    // ---------------------------------------------------------------- startup line

    /// <summary>"-mod=@A;@B" style options, lower-cased keys, quotes removed.</summary>
    public static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in args)
        {
            var a = raw.Trim().Trim('"');
            if (!a.StartsWith('-')) continue;
            int eq = a.IndexOf('=');
            if (eq < 0) { opts[a[1..]] = ""; continue; }
            opts[a[1..eq]] = a[(eq + 1)..].Trim().Trim('"');
        }
        return opts;
    }

    public static IEnumerable<string> SplitModList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Trim('"'));

    private static string Resolve(string root, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));

    /// <summary>Splits a Windows command line the way Windows itself does (CommandLineToArgvW rules).</summary>
    public static List<string> SplitWindowsCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, hasToken = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { slashes++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }
                hasToken = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) { args.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken) args.Add(current.ToString());
        return args;
    }

    // ---------------------------------------------------------------- files

    public sealed record ServerCfg(string? Hostname = null, int? MaxPlayers = null, int? SteamQueryPort = null, string? Map = null);

    public static ServerCfg ReadServerCfg(string text)
    {
        var clean = StripComments(text);
        string? Str(string key) => Regex.Match(clean, $@"\b{key}\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        int? Int(string key) => Regex.Match(clean, $@"\b{key}\s*=\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

        var template = Str("template");
        string? map = template != null && template.Contains('.') ? template[(template.LastIndexOf('.') + 1)..] : null;
        return new ServerCfg(Str("hostname"), Int("maxPlayers"), Int("steamQueryPort"), map);
    }

    public sealed record Meta(ulong Id, string? Name);

    public static Meta ReadMeta(string path)
    {
        if (!File.Exists(path)) return new Meta(0, null);
        var text = File.ReadAllText(path);
        var id = Regex.Match(text, @"\bpublishedid\s*=\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } m && ulong.TryParse(m.Groups[1].Value, out var v) ? v : 0;
        var name = Regex.Match(text, @"\bname\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase) is { Success: true } n ? n.Groups[1].Value : null;
        return new Meta(id, name);
    }

    /// <summary>Removes // and /* */ comments, leaving anything inside quotes alone.</summary>
    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') inString = !inString;
            if (!inString && c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; sb.Append('\n'); continue; }
                if (text[i + 1] == '*') { int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 1; continue; }
            }
            if (c == '\n') inString = false; // unterminated quote never runs past a line
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- process lists

    private static IEnumerable<ServerProcess> WindowsProcesses(HashSet<string> names)
    {
        var where = string.Join(" OR ", names.Select(n => $"Name = '{n.Replace("'", "")}.exe'"));
        var found = new List<ServerProcess>();
        using var searcher = new ManagementObjectSearcher($"SELECT ProcessId, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE {where}");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                int pid = Convert.ToInt32(mo["ProcessId"]);
                var exe = mo["ExecutablePath"] as string;
                var cmd = mo["CommandLine"] as string;
                DateTime? started = null;
                try { if (mo["CreationDate"] is string cd) started = ManagementDateTimeConverter.ToDateTime(cd); } catch { }
                if (exe == null)
                {
                    try { exe = Process.GetProcessById(pid).MainModule?.FileName; } catch { }
                }
                found.Add(new ServerProcess(pid, exe ?? "", cmd == null || exe == null ? null : SplitWindowsCommandLine(cmd).Skip(1).ToList(), started));
            }
        }
        return found;
    }

    /// <summary>Linux: also used to test the agent without Windows.</summary>
    private static IEnumerable<ServerProcess> LinuxProcesses(HashSet<string> names)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            string[] argv;
            try { argv = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries); }
            catch { continue; }
            // argv[0] for a real binary, argv[1] when it's started through an interpreter.
            int exeIndex = Array.FindIndex(argv, 0, Math.Min(2, argv.Length), a => names.Contains(Path.GetFileNameWithoutExtension(a)));
            if (exeIndex < 0) continue;
            DateTime? started = null;
            try { started = Process.GetProcessById(pid).StartTime; } catch { }
            var exe = argv[exeIndex];
            if (!Path.IsPathRooted(exe))
            {
                try { exe = Path.Combine(new DirectoryInfo(Path.Combine(dir, "cwd")).ResolveLinkTarget(true)?.FullName ?? "", exe); } catch { }
            }
            yield return new ServerProcess(pid, exe, argv.Skip(exeIndex + 1).ToList(), started);
        }
    }
}
