using System.Diagnostics;
using System.Reflection;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Updates;

namespace MMLauncher.App.Updates;

/// <summary>
/// Replaces the running MMLauncher.exe with a newer release and restarts it.
/// Windows lets a running exe be renamed (not overwritten), so: download + verify the new exe,
/// rename the running one to MMLauncher.exe.old, put the new one in its place, start it, exit.
/// The new copy deletes the .old file when it starts.
/// </summary>
public static class SelfUpdater
{
    public const string AssetName = "MMLauncher.exe";
    public const string UpdatedArgument = "--updated";

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static string? ExePath => Environment.ProcessPath;

    /// <summary>
    /// True when running as the published MMLauncher.exe from a folder we can write to.
    /// (A developer build run through "dotnet", or an exe in a protected folder, can't swap itself.)
    /// </summary>
    public static bool CanSelfUpdate
    {
        get
        {
            if (!OperatingSystem.IsWindows() || ExePath == null) return false;
            if (!Path.GetFileName(ExePath).Equals(AssetName, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var probe = Path.Combine(Path.GetDirectoryName(ExePath)!, $".mml-write-test-{Environment.ProcessId}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Removes leftovers from a previous update. Safe to call on every start.</summary>
    public static void CleanUp()
    {
        try
        {
            if (ExePath != null)
            {
                var old = ExePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            var dir = UpdateFolder;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* still in use right after restart; next start gets it */ }
    }

    private static string UpdateFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMLauncher", "update");

    /// <summary>Downloads, verifies and installs the release, then restarts. Returns only on failure.</summary>
    public static async Task ApplyAsync(UpdateChecker checker, ReleaseInfo release, IProgress<double> progress, CancellationToken ct = default)
    {
        if (!CanSelfUpdate || ExePath == null)
            throw new InvalidOperationException("This copy of the launcher can't update itself here. Download the new version from the releases page.");

        var exe = ExePath;
        var downloaded = Path.Combine(UpdateFolder, $"MMLauncher-{release.Version}.exe");
        Log.Info($"Updating launcher {CurrentVersion} -> {release.Version}");
        await checker.DownloadVerifiedAsync(release, AssetName, downloaded, progress, ct);

        var old = exe + ".old";
        try { if (File.Exists(old)) File.Delete(old); } catch { }
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe); // put the original back; nothing lost
            throw;
        }

        Log.Info($"Launcher updated to {release.Version}; restarting.");
        Process.Start(new ProcessStartInfo(exe, UpdatedArgument) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        Environment.Exit(0);
    }
}
