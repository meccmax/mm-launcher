using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MMLauncher.Core.Logging;

namespace MMLauncher.Core.Launch;

/// <summary>
/// Spots the most common cause of DayZ's "Unable to locate a running instance of Steam": Steam
/// and the launcher running at different permission levels (one "as administrator", the other
/// not). Windows keeps them apart, so DayZ started by the launcher can't reach Steam.
/// </summary>
public static class SteamPermissions
{
    public enum Problem { None, SteamIsAdmin, LauncherIsAdmin }

    /// <summary>Checks this PC right now. Never throws; "None" when it can't tell.</summary>
    public static Problem Check()
    {
        if (!OperatingSystem.IsWindows()) return Problem.None;
        try
        {
            bool me = LauncherIsElevated();
            var steam = Process.GetProcessesByName("steam").FirstOrDefault();
            if (steam == null) return Problem.None;
            bool? steamElevated = IsElevated(steam.Id, out bool denied);
            // Without admin rights we're not allowed to look at an admin process's permissions,
            // which itself means Steam runs as administrator (or as another Windows user).
            if (steamElevated == null && denied && !me) steamElevated = true;
            var problem = Diagnose(me, steamElevated);
            Log.Info($"Permissions: launcher {(me ? "as administrator" : "normal")}, Steam " +
                     $"{(steamElevated == null ? "unknown" : steamElevated.Value ? "as administrator" : "normal")}" +
                     (problem == Problem.None ? "" : $" -> {problem}"));
            return problem;
        }
        catch (Exception ex)
        {
            Log.Info("Permissions check skipped: " + ex.Message);
            return Problem.None;
        }
    }

    public static Problem Diagnose(bool launcherElevated, bool? steamElevated) => steamElevated switch
    {
        true when !launcherElevated => Problem.SteamIsAdmin,
        false when launcherElevated => Problem.LauncherIsAdmin,
        _ => Problem.None,
    };

    public static string Explain(Problem problem) => problem switch
    {
        Problem.SteamIsAdmin =>
            "Steam is running as administrator (or as another Windows user), but MM Launcher isn't. Windows keeps them apart, so DayZ " +
            "would stop with \"Unable to locate a running instance of Steam\".\n\nExit Steam from its tray icon and start it normally. " +
            "If it keeps starting as administrator, right-click its shortcut > Properties > Compatibility and untick " +
            "\"Run this program as an administrator\".",
        Problem.LauncherIsAdmin =>
            "MM Launcher is running as administrator, but Steam isn't. Windows keeps them apart, so DayZ would stop with " +
            "\"Unable to locate a running instance of Steam\".\n\nClose MM Launcher and open it again normally from the Start menu " +
            "(not \"Run as administrator\"; check its shortcut's Properties > Compatibility too).",
        _ => "",
    };

    private static bool LauncherIsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool? IsElevated(int pid, out bool denied)
    {
        denied = false;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) { denied = Marshal.GetLastWin32Error() == 5; return null; }
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) { denied = Marshal.GetLastWin32Error() == 5; return null; }
            try
            {
                return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) ? elevated != 0 : null;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
