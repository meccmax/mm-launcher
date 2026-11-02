using MMLauncher.Core.Friends;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// The in-game name. DayZ only gets a name from its own launcher; started from MM Launcher
/// without -name=, the player is "Survivor". So the launcher asks for one on start-up and before
/// joining while it's still empty, suggesting the player's Steam name.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Set by the window: (message, suggested name, OK text, later text) → the name, or null for later.</summary>
    public Func<string, string, string, string, Task<string?>>? AskName { get; set; }

    /// <summary>"Join as Survivor" was picked: don't ask again at join until the launcher restarts.
    /// ("Later" on start-up still asks at the next join, the moment it matters.)</summary>
    private bool _nameDismissed;

    private const string NameWhy =
        "DayZ only gets your name from its own launcher, so when MM Launcher starts the game without one, " +
        "you show up as \"Survivor\". Pick the name other players and staff will see.";

    /// <summary>Asks for a name if there isn't a usable one yet. Never stops a join.</summary>
    private async Task EnsurePlayerNameAsync(bool joining)
    {
        if (AskName == null || (joining && _nameDismissed) || PlayerNames.IsUsable(_service.Settings.PlayerName)) return;
        var steamName = PlayerNames.Clean(SteamAccount.PersonaName(_steam?.SteamPath));
        var suggested = PlayerNames.IsUsable(steamName) ? steamName : "";
        var name = await AskName(NameWhy, suggested,
            joining ? "Save and join" : "Save name",
            joining ? "Join as Survivor" : "Later");
        if (name == null)
        {
            if (joining) _nameDismissed = true;
            Log.Info("In-game name: not set (player chose later).");
            return;
        }
        var s = Core.Config.LauncherSettings.Load();
        s.PlayerName = name;
        s.Save();
        _service.ReloadSettings();
        Log.Info("In-game name set.");
        StatusMessage = $"In-game name: {name}";
    }
}
