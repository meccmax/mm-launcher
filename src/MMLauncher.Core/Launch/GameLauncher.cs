using System.Diagnostics;
using MMLauncher.Core.Config;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Steam;

namespace MMLauncher.Core.Launch;

/// <summary>Everything needed to start DayZ and connect to one server.</summary>
public sealed record LaunchRequest(string ServerIp, int GamePort, IReadOnlyList<RequiredMod> Mods, string? Password);

/// <summary>The command that was (or would be) run.</summary>
public sealed record LaunchCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public override string ToString() => $"\"{FileName}\" " + string.Join(" ", Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}

/// <summary>Thrown with a player-friendly message when DayZ can't be started.</summary>
public sealed class LaunchException(string message) : Exception(message);

/// <summary>
/// Starts DayZ connected to a server with exactly that server's mods, in its load order.
/// </summary>
public static class GameLauncher
{
    public const string JunctionFolder = "!MMWorkshop";

    /// <summary>Set MMLAUNCHER_DRY_RUN=1 to log the command instead of starting DayZ (testing).</summary>
    public static bool DryRun => Environment.GetEnvironmentVariable("MMLAUNCHER_DRY_RUN") == "1";

    public static bool DayZIsRunning() =>
        Process.GetProcessesByName("DayZ_x64").Length > 0 || Process.GetProcessesByName("DayZ_BE").Length > 0;

    public static bool SteamIsRunning() =>
        !OperatingSystem.IsWindows() || Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Waits until DayZ_BE.exe or DayZ_x64.exe shows up, or the timeout passes.</summary>
    public static async Task<bool> WaitForDayZAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (DayZIsRunning()) return true;
            await Task.Delay(1000);
        }
        return DayZIsRunning();
    }

    public static LaunchCommand Build(LaunchRequest request, LauncherSettings settings, LocalSteam steam)
    {
        if (steam.DayZPath == null)
            throw new LaunchException("DayZ isn't installed in any Steam library on this PC.");

        // Every mod must be on disk before we get here (the sync step makes sure of it).
        var folders = new List<(RequiredMod Mod, string Folder)>();
        foreach (var mod in request.Mods)
        {
            if (!steam.Mods.TryGetValue(mod.Id, out var installed))
                throw new LaunchException($"{mod.Name} ({mod.Id}) isn't downloaded yet.");
            folders.Add((mod, installed.Folder));
        }

        var modEntries = settings.UseJunctions
            ? TryJunctions(steam.DayZPath, folders) ?? folders.Select(f => f.Folder).ToList()
            : folders.Select(f => f.Folder).ToList();

        var gameArgs = new List<string>
        {
            $"-connect={request.ServerIp}",
            $"-port={request.GamePort}",
        };
        if (modEntries.Count > 0) gameArgs.Add("-mod=" + string.Join(";", modEntries));
        if (!string.IsNullOrWhiteSpace(settings.PlayerName)) gameArgs.Add($"-name={settings.PlayerName}");
        if (!string.IsNullOrEmpty(request.Password)) gameArgs.Add($"-password={request.Password}");
        gameArgs.AddRange(settings.ExtraLaunchParameters.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (settings.LaunchDirect)
        {
            // DayZ_BE.exe is BattlEye's launcher: it starts the BattlEye service, then runs the
            // game named after -exe with the remaining arguments. Starting the game any other
            // way can leave BattlEye off, and BattlEye-protected servers then kick you
            // (0x000400F0). "-exe DayZ_x64.exe" is the form Bohemia staff give for shortcuts.
            var exe = Path.Combine(steam.DayZPath, "DayZ_BE.exe");
            if (!File.Exists(exe)) throw new LaunchException($"Couldn't find {exe}. Try \"LaunchMethod\": \"Steam\" in settings.jsonc.");
            var beArgs = new List<string> { "-exe", "DayZ_x64.exe" };
            beArgs.AddRange(gameArgs.Where(a => !a.Equals("-nolauncher", StringComparison.OrdinalIgnoreCase)));
            return new LaunchCommand(exe, beArgs, steam.DayZPath);
        }

        var steamExe = Path.Combine(steam.SteamPath, OperatingSystem.IsWindows() ? "steam.exe" : "steam");
        var args = new List<string> { "-applaunch", LocalSteam.DayZAppId };
        args.AddRange(gameArgs);
        return new LaunchCommand(steamExe, args, steam.DayZPath);
    }

    public static void Start(LaunchCommand command)
    {
        // Never log the password.
        Log.Info("Launch: " + System.Text.RegularExpressions.Regex.Replace(command.ToString(), "-password=[^ \"]*", "-password=***"));
        if (DryRun)
        {
            Log.Info("Dry run: DayZ not started.");
            return;
        }
        // ShellExecute so Windows can show the admin prompt BattlEye sometimes needs the first
        // time it installs or updates its service.
        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = true,
            WorkingDirectory = command.WorkingDirectory,
            Arguments = string.Join(" ", command.Arguments.Select(Quote)),
        };
        Process.Start(psi);
    }

    /// <summary>Quotes one argument for a Windows command line (CommandLineToArgvW rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Makes DayZ\!MMWorkshop\@&lt;id&gt; junctions pointing at Steam's workshop folders, the
    /// same trick the official launcher uses (!Workshop). Short relative paths with no
    /// spaces survive being passed through Steam. Returns null if junctions can't be made,
    /// and the caller falls back to full paths.
    /// </summary>
    private static List<string>? TryJunctions(string dayzPath, List<(RequiredMod Mod, string Folder)> mods)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var root = Path.Combine(dayzPath, JunctionFolder);
        try
        {
            Directory.CreateDirectory(root);
            var entries = new List<string>();
            foreach (var (mod, target) in mods)
            {
                var name = "@" + mod.Id;
                var link = Path.Combine(root, name);
                if (!EnsureJunction(link, target))
                {
                    Log.Warn($"Couldn't create junction {link}; using full paths instead.");
                    return null;
                }
                entries.Add(Path.Combine(JunctionFolder, name));
            }
            return entries;
        }
        catch (Exception ex)
        {
            Log.Warn($"Junction setup failed ({ex.Message}); using full paths instead.");
            return null;
        }
    }

    private static bool EnsureJunction(string link, string target)
    {
        var info = new DirectoryInfo(link);
        if (info.Exists)
        {
            var current = info.LinkTarget;
            if (current == null) return false; // a real folder is there: never touch it
            if (string.Equals(Path.GetFullPath(current).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;
            Directory.Delete(link); // non-recursive: removes the link only, never the mod
        }
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        return p.ExitCode == 0 && Directory.Exists(link);
    }
}
