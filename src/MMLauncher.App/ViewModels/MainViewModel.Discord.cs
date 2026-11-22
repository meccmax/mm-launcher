using Avalonia.Threading;
using MMLauncher.Core.Discord;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// "Playing MM Launcher" on the player's Discord profile: browsing servers, which MM server
/// they're on, or the DayZ Editor. Other servers show only "Playing DayZ on a community server",
/// never their name. Off with Settings > "Show what I'm playing on Discord".
/// </summary>
public partial class MainViewModel
{
    private readonly DiscordPresence _discord = new();
    private readonly DispatcherTimer _discordTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _inGameStatus;
    private int _gameGoneTicks;

    private async Task StartDiscordAsync()
    {
        _discordTimer.Tick += async (_, _) => await DiscordTickAsync();
        _discordTimer.Start();
        await ShowBrowsingStatusAsync();
        await _discord.SetEnabledAsync(_service.Settings.DiscordStatus);
    }

    /// <summary>Reconnects if Discord was started later, and goes back to "Browsing" once DayZ has closed.</summary>
    private async Task DiscordTickAsync()
    {
        try
        {
            if (_inGameStatus)
            {
                _gameGoneTicks = GameLauncher.DayZIsRunning() ? 0 : _gameGoneTicks + 1;
                if (_gameGoneTicks >= 2) await ShowBrowsingStatusAsync();
            }
            await _discord.TickAsync();
        }
        catch (Exception ex) { Log.Warn("Discord status: " + ex.Message); }
    }

    private Task ShowBrowsingStatusAsync()
    {
        _inGameStatus = false;
        return _discord.SetAsync(new PresenceStatus("Browsing servers", "Misfit Mercenaries DayZ", DateTimeOffset.Now));
    }

    private Task ShowPlayingStatusAsync(ServerViewModel server)
    {
        _inGameStatus = true;
        _gameGoneTicks = 0;
        return IsMmServer(server)
            ? _discord.SetAsync(new PresenceStatus($"Playing on {server.Name}", string.IsNullOrEmpty(server.Map) ? "DayZ" : server.MapText, DateTimeOffset.Now))
            : _discord.SetAsync(new PresenceStatus("Playing DayZ", "On a community server", DateTimeOffset.Now));
    }

    private Task ShowEditorStatusAsync()
    {
        _inGameStatus = true;
        _gameGoneTicks = 0;
        return _discord.SetAsync(new PresenceStatus("In the DayZ Editor", "Building something", DateTimeOffset.Now));
    }

    private Task ApplyDiscordSettingAsync() => _discord.SetEnabledAsync(_service.Settings.DiscordStatus);

    // ------------------------------------------------------------------ damaged DayZ settings file

    /// <summary>
    /// Before starting DayZ: if one of DayZ's own settings files is damaged (DayZ would stop with
    /// "…encountered instead of '='"), offer to set it aside so DayZ makes a fresh one. Never blocks the launch.
    /// </summary>
    private async Task CheckDayZSettingsFilesAsync(bool ask)
    {
        List<string> damaged;
        try { damaged = await Task.Run(() => DayZSettingsCheck.FindDamaged()); }
        catch { return; }
        if (damaged.Count == 0) return;
        var names = string.Join(", ", damaged.Select(Path.GetFileName));
        Log.Warn($"DayZ settings file(s) look damaged: {names}");
        if (!ask || AskYesNo == null) return;
        bool fix = await AskYesNo("DayZ's settings file is damaged",
            $"{names} in {DayZSettingsCheck.Folder} is full of junk characters (usually from a crash or a cloud sync while DayZ was saving). " +
            "DayZ will show an error and may not start.\n\nFix it now? The launcher renames the damaged file (nothing is deleted), and DayZ makes a fresh one " +
            "with default video and audio settings. Your key bindings are kept.",
            "Fix it", "Start anyway");
        if (!fix) return;
        var failed = damaged.Select(f => (File: f, Why: DayZSettingsCheck.SetAside(f))).Where(r => r.Why != null).ToList();
        if (failed.Count > 0)
            ShowResult(false, "Couldn't fix DayZ's settings file", $"{Path.GetFileName(failed[0].File)}: {failed[0].Why}. Close DayZ and anything syncing that folder (like OneDrive), then try again.");
    }
}
