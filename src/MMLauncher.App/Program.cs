using Avalonia;

namespace MMLauncher.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Steam download helper mode: no window, talks to the main launcher over stdin/stdout,
        // exits when done so Steam stops treating DayZ as running. See SyncHelperProcess.
        if (args.Length > 0 && args[0] == Steam.SyncHelperProcess.Argument)
            return Steam.SyncHelperProcess.RunAsync().GetAwaiter().GetResult();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Last-resort crash log so a player can send it to staff.
            var path = Path.Combine(Core.Config.LauncherSettings.DataFolder, "launcher-crash.txt");
            File.WriteAllText(path, $"{DateTime.Now:u}\n{ex}");
            throw;
        }
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
