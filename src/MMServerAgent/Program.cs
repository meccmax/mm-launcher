using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using MMServerAgent;

// MMServerAgent.exe            run the agent (in this window, or as the service)
// MMServerAgent.exe check      show what the agent sees right now, then exit
// MMServerAgent.exe install    install + start as a Windows service, open the firewall port (admin)
// MMServerAgent.exe uninstall  stop + remove the service and firewall rule (admin)

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
    case "help" or "?" or "h":
        Console.WriteLine("Usage: MMServerAgent.exe [check | install | uninstall]");
        return 0;
}

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
    if (OperatingSystem.IsWindows() && args.All(a => a != "--no-pause"))
    {
        Console.WriteLine("Press any key to close.");
        Console.ReadKey(true);
    }
    return 0;
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
