using MMLauncher.Core.Config;

namespace MMLauncher.Core.Logging;

/// <summary>
/// Simple append-only log at %APPDATA%\MMLauncher\launcher.log. Every sync and launch step
/// goes here so a player (or staff) can send it when something goes wrong.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(LauncherSettings.DataFolder, "launcher.log");

    /// <summary>Raised for every line, so the window can show recent activity.</summary>
    public static event Action<string>? Written;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level}  {message}";
        lock (Gate)
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { /* logging must never break the launcher */ }
        }
        try { Written?.Invoke(line); } catch { }
    }
}
