using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using MMLauncher.Core.Updates;
using MMServerAgent;

// MMServerAgent.exe            run the agent (in this window, or as the service)
// MMServerAgent.exe check      show what the agent sees right now, then exit
// MMServerAgent.exe install    install + start as a Windows service, open the firewall port (admin)
// MMServerAgent.exe uninstall  stop + remove the service and firewall rule (admin)
// MMServerAgent.exe update     download the newest agent from GitHub, verify it, swap it in (admin)
//                              The agent never updates itself; this only runs when you run it.

const string ServiceName = "MMServerAgent";
const string DisplayName = "MM Server Agent";
var command = args.FirstOrDefault()?.Trim().TrimStart('-', '/').ToLowerInvariant() ?? "";

switch (command)
{
    case "check":
        return Check();
    case "install":
        return Install();
    case "uninstall":
        return Uninstall();
    case "update":
        return await UpdateAsync();
    case "help" or "?" or "h":
        Console.WriteLine("Usage: MMServerAgent.exe [check | install | uninstall | update]");
        return 0;
}

// Leftover from a previous "update" (the old exe is renamed, then removed here).
try { if (Environment.ProcessPath is { } self && File.Exists(self + ".old")) File.Delete(self + ".old"); } catch { }

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
builder.Services.AddHostedService<AgentWorker>();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
if (!WindowsServiceHelpers.IsWindowsService())
    Console.WriteLine("Running in this window. Close it or press Ctrl+C to stop. (Use \"install\" to run it as a service instead.)");
await builder.Build().RunAsync();
return 0;

int Check()
{
    var config = AgentConfig.Load();
    Console.WriteLine($"MM Server Agent {AgentWorker.Version}  ·  port {config.Port}  ·  config {AgentConfig.FilePath}");
    Console.WriteLine();
    var d = ServerDetector.Detect(config);
    if (d.Servers.Count == 0) Console.WriteLine("No running DayZ servers found.");
    foreach (var s in d.Servers)
    {
        Console.WriteLine($"[{s.Name}]");
        Console.WriteLine($"  game port {s.GamePort}  ·  query port {s.QueryPort}  ·  max {s.MaxPlayers}  ·  map {s.Map}  ·  up {Uptime(s.UptimeSeconds)}");
        if (s.DisplayName != null) Console.WriteLine($"  display name: {s.DisplayName}");
        if (s.Message != null) Console.WriteLine($"  message: {s.Message}");
        Console.WriteLine($"  {s.Mods.Count} client mods:");
        foreach (var m in s.Mods)
            Console.WriteLine($"    {(m.Id == 0 ? "(no id)" : m.Id.ToString()),-12} {m.Name}  [{m.Folder}]");
        Console.WriteLine();
    }
    foreach (var w in d.Warnings)
        Console.WriteLine("WARNING: " + w);

    // Just tells you; never installs anything.
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var latest = new UpdateChecker(http).GetLatestAsync().GetAwaiter().GetResult();
        if (latest != null && UpdateChecker.IsNewer(latest.Version, CurrentVersion()))
            Console.WriteLine($"\nA newer agent is available: v{latest.Version} (you have v{CurrentVersion()}). Run update-agent.bat as administrator to install it.");
    }
    catch { }
    if (OperatingSystem.IsWindows() && args.All(a => a != "--no-pause"))
    {
        Console.WriteLine("Press any key to close.");
        Console.ReadKey(true);
    }
    return 0;
}

static Version CurrentVersion() =>
    UpdateChecker.TryParseVersion(AgentWorker.Version, out var v) ? v : new Version(0, 0, 0);

async Task<int> UpdateAsync()
{
    if (!OperatingSystem.IsWindows()) { Console.WriteLine("update is Windows-only."); return 1; }
    var exe = Environment.ProcessPath!;
    var dir = Path.GetDirectoryName(exe)!;
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var checker = new UpdateChecker(http);

    Console.WriteLine($"MM Server Agent v{CurrentVersion()}: checking GitHub ({UpdateChecker.Repository})...");
    ReleaseInfo? latest;
    try { latest = await checker.GetLatestAsync(); }
    catch (Exception ex) { Console.WriteLine("Couldn't reach GitHub: " + ex.Message); return 1; }
    if (latest == null) { Console.WriteLine("No releases found."); return 1; }
    if (!UpdateChecker.IsNewer(latest.Version, CurrentVersion()))
    {
        Console.WriteLine($"Already up to date (v{CurrentVersion()}).");
        return 0;
    }

    Console.WriteLine($"Updating v{CurrentVersion()} -> v{latest.Version}");
    if (!string.IsNullOrWhiteSpace(latest.Notes))
        Console.WriteLine("\nWhat's new:\n" + latest.Notes.Trim() + "\n");

    var work = Path.Combine(Path.GetTempPath(), $"MMServerAgent-update-{latest.Version}");
    var zipPath = Path.Combine(work, "MMServerAgent.zip");
    try
    {
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Console.Write("Downloading and verifying... ");
        await checker.DownloadVerifiedAsync(latest, "MMServerAgent.zip", zipPath);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, work);
        Console.WriteLine("ok");
    }
    catch (Exception ex)
    {
        Console.WriteLine("\nDownload failed: " + ex.Message + "\nNothing was changed.");
        return 1;
    }

    var newRoot = Directory.GetFiles(work, "MMServerAgent.exe", SearchOption.AllDirectories).FirstOrDefault();
    if (newRoot == null) { Console.WriteLine("The download didn't contain MMServerAgent.exe. Nothing was changed."); return 1; }
    var newDir = Path.GetDirectoryName(newRoot)!;

    bool serviceExists = Run("sc.exe", $"query {ServiceName}", quiet: true) == 0;
    if (serviceExists)
    {
        Console.Write("Stopping the service... ");
        Run("sc.exe", $"stop {ServiceName}", quiet: true);
        for (int i = 0; i < 30 && !ServiceStopped(); i++) Thread.Sleep(1000);
        Console.WriteLine(ServiceStopped() ? "stopped" : "still stopping (continuing)");
    }

    // A running exe can be renamed but not overwritten: move this one aside, put the new one in place.
    var old = exe + ".old";
    try
    {
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        File.Copy(newRoot, exe);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Couldn't replace the exe: " + ex.Message + " (Run update-agent.bat as administrator.)");
        try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
        if (serviceExists) Run("sc.exe", $"start {ServiceName}", quiet: true);
        return 1;
    }

    // Refresh the helper .bat files and the setup notes. agent.jsonc, agent.log and reports\ are never touched.
    foreach (var f in Directory.GetFiles(newDir))
    {
        var name = Path.GetFileName(f);
        if (name.Equals("MMServerAgent.exe", StringComparison.OrdinalIgnoreCase)) continue;
        if (name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            try { File.Copy(f, Path.Combine(dir, name), overwrite: true); } catch { }
    }
    try { Directory.Delete(work, true); } catch { }

    if (serviceExists)
    {
        Console.Write("Starting the service... ");
        Console.WriteLine(Run("sc.exe", $"start {ServiceName}", quiet: true) == 0 ? "started" : "couldn't start; check services.msc");
    }
    Console.WriteLine($"\nDone. MM Server Agent is now v{latest.Version}. Your agent.jsonc and reports were left as they were.");
    return 0;
}

static bool ServiceStopped()
{
    var psi = new ProcessStartInfo("sc.exe", $"query {ServiceName}") { UseShellExecute = false, RedirectStandardOutput = true };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase);
}

static string Uptime(long secs) => secs < 3600 ? $"{secs / 60} min" : secs < 86400 ? $"{secs / 3600}h {secs % 3600 / 60}m" : $"{secs / 86400}d {secs % 86400 / 3600}h";

int Install()
{
    if (!OperatingSystem.IsWindows()) { Console.WriteLine("install is Windows-only."); return 1; }
    var config = AgentConfig.Load();
    var exe = Environment.ProcessPath!;
    Run("sc.exe", $"stop {ServiceName}", quiet: true);
    Run("sc.exe", $"delete {ServiceName}", quiet: true);
    Thread.Sleep(1500);
    if (Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto DisplayName= \"{DisplayName}\"") != 0)
    {
        Console.WriteLine("Couldn't create the service. Right-click install-agent.bat and choose \"Run as administrator\".");
        return 1;
    }
    Run("sc.exe", $"description {ServiceName} \"Tells the MM Launcher which mods the DayZ servers on this machine run. Read-only.\"");
    Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
    Run("netsh", $"advfirewall firewall delete rule name=\"{DisplayName}\"", quiet: true);
    Run("netsh", $"advfirewall firewall add rule name=\"{DisplayName}\" dir=in action=allow protocol=TCP localport={config.Port}");
    Run("sc.exe", $"start {ServiceName}");
    Console.WriteLine();
    Console.WriteLine($"Installed. The agent now runs in the background on TCP port {config.Port} and starts with Windows.");
    Console.WriteLine($"If this machine is behind a router, forward TCP {config.Port} to it.");
    Console.WriteLine($"Test from any browser: http://<this machine's public address>:{config.Port}/");
    return 0;
}

int Uninstall()
{
    if (!OperatingSystem.IsWindows()) { Console.WriteLine("uninstall is Windows-only."); return 1; }
    Run("sc.exe", $"stop {ServiceName}", quiet: true);
    Run("sc.exe", $"delete {ServiceName}");
    Run("netsh", $"advfirewall firewall delete rule name=\"{DisplayName}\"", quiet: true);
    Console.WriteLine("Removed.");
    return 0;
}

static int Run(string file, string arguments, bool quiet = false)
{
    var psi = new ProcessStartInfo(file, arguments) { UseShellExecute = false, RedirectStandardOutput = quiet, RedirectStandardError = quiet };
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode;
}
