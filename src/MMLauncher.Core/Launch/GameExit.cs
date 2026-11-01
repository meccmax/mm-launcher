namespace MMLauncher.Core.Launch;

/// <summary>
/// Tells a DayZ crash from a normal quit, so "rejoin if DayZ crashes" never brings back
/// someone who logged off on purpose.
///
/// A crash leaves a crash dump (DayZ_x64_*.mdmp) in DayZ's log folder, or ends with a
/// Windows exception code (0xC0000005 and friends). DayZ's "crash_*.log" is NOT used: it
/// also records script errors that don't close the game.
/// </summary>
public static class GameExit
{
    public static string LogFolder =>
        Environment.GetEnvironmentVariable("MMLAUNCHER_TEST_DAYZLOGS") is { Length: > 0 } t ? t
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DayZ");

    /// <param name="exitCode">DayZ_x64.exe's exit code, if it could be read.</param>
    /// <param name="startedUtc">When the launcher started DayZ.</param>
    public static bool LooksLikeCrash(int? exitCode, DateTime startedUtc, string? logFolder = null)
    {
        if (exitCode is int code && IsExceptionCode(code)) return true;
        try
        {
            var folder = logFolder ?? LogFolder;
            if (!Directory.Exists(folder)) return false;
            return Directory.EnumerateFiles(folder, "*.mdmp")
                .Any(f => File.GetLastWriteTimeUtc(f) > startedUtc.AddSeconds(-5));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Windows "the program crashed" codes (NTSTATUS errors, 0xC0000000 and up).</summary>
    public static bool IsExceptionCode(int code) => unchecked((uint)code) >= 0xC0000000;
}
