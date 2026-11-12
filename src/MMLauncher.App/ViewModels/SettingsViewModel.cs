using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Config;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// The Settings window. Edits a copy of the settings; nothing is written until Save.
/// Folder fields left empty mean "find it automatically"; the line under each field shows
/// what will actually be used and whether it looks right.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherSettings _original;

    public SettingsViewModel(LauncherSettings settings)
    {
        _original = settings;
        _steamPath = settings.SteamPathOverride;
        _dayZPath = settings.DayZPathOverride;
        _workshopPath = settings.WorkshopPathOverride;
        _launchMethodIndex = settings.LaunchDirect ? 0 : 1;
        _playerName = settings.PlayerName;
        _extraParameters = settings.ExtraLaunchParameters;
        _displayModeIndex = settings.DisplayMode switch { Core.Launch.GameDisplay.Fullscreen => 1, Core.Launch.GameDisplay.Windowed => 2, _ => 0 };
        var dayz = Core.Launch.GameDisplay.Read();
        _dayZWindowNow = dayz.Width > 0 && dayz.Height > 0 ? $"{dayz.Width} × {dayz.Height}" : "";
        _dayZModeNow = dayz.Windowed switch { true => "windowed", false => "fullscreen", null => "" };
        _windowWidth = settings.WindowWidth > 0 ? settings.WindowWidth : dayz.Width > 0 ? dayz.Width : 1920;
        _windowHeight = settings.WindowHeight > 0 ? settings.WindowHeight : dayz.Height > 0 ? dayz.Height : 1080;
        _autoRefreshSeconds = settings.AutoRefreshSeconds;
        _checkForUpdates = settings.CheckForUpdates;
        _originalTheme = Themes.AppTheme.Find(settings.Theme);
        _themeIndex = Math.Max(0, Themes.AppTheme.All.ToList().IndexOf(_originalTheme));
        _usageStatsOn = settings.UsageStats == "on";
        _usageWasAsked = settings.UsageStats != "ask";
        Recheck();
    }

    /// <summary>Set by the window: opens a folder picker, returns the chosen path or null.</summary>
    public Func<string, string?, Task<string?>>? PickFolder { get; set; }

    /// <summary>Set by the window: closes it.</summary>
    public Action<bool>? Close { get; set; }

    /// <summary>True after Save, so the main window knows to reload.</summary>
    public bool Saved { get; private set; }

    // Editable values ("" = automatic)
    [ObservableProperty] private string _steamPath;
    [ObservableProperty] private string _dayZPath;
    [ObservableProperty] private string _workshopPath;
    [ObservableProperty] private int _launchMethodIndex; // 0 = Direct, 1 = Steam
    [ObservableProperty] private string _playerName;
    [ObservableProperty] private string _extraParameters;
    [ObservableProperty] private int _displayModeIndex; // 0 = DayZ's own, 1 = fullscreen, 2 = windowed
    [ObservableProperty] private decimal? _windowWidth;
    [ObservableProperty] private decimal? _windowHeight;
    [ObservableProperty] private int _windowSizeIndex = -1;
    private readonly string _dayZWindowNow;
    private readonly string _dayZModeNow;

    public string[] DisplayModes { get; } = ["Use DayZ's own setting", "Fullscreen", "Windowed"];
    public static readonly (int W, int H)[] CommonSizes = [(1280, 720), (1600, 900), (1920, 1080), (2560, 1080), (2560, 1440), (3440, 1440), (3840, 2160)];
    public string[] WindowSizes { get; } = CommonSizes.Select(s => $"{s.W} × {s.H}").ToArray();
    public bool IsWindowedChoice => DisplayModeIndex == 2;
    public string DisplayHelp => DisplayModeIndex switch
    {
        1 => "DayZ opens fullscreen, at your desktop resolution (DayZ always uses that in fullscreen; its own \"rendering resolution\" option scales it).",
        2 => "DayZ opens in a window of this size." + (_dayZWindowNow.Length > 0 ? $" Its window is {_dayZWindowNow} now." : ""),
        _ => "Nothing is changed: DayZ opens the way you last left it" + (_dayZModeNow.Length > 0 ? $" ({_dayZModeNow}" + (_dayZModeNow == "windowed" && _dayZWindowNow.Length > 0 ? $", {_dayZWindowNow})." : ").") : "."),
    } + (DisplayModeIndex == 0 ? "" : " Set in DayZ's own settings file (Documents\\DayZ\\DayZ.cfg) each time the launcher starts DayZ, so it wins over a change made in-game. Only these keys are touched, and the original file is kept as DayZ.cfg.mmlauncher-backup.");

    partial void OnDisplayModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsWindowedChoice));
        OnPropertyChanged(nameof(DisplayHelp));
    }

    partial void OnWindowSizeIndexChanged(int value)
    {
        if (value < 0 || value >= CommonSizes.Length) return;
        WindowWidth = CommonSizes[value].W;
        WindowHeight = CommonSizes[value].H;
    }
    [ObservableProperty] private decimal? _autoRefreshSeconds;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string _updateStatus = "";

    // ---- appearance: picking a theme shows it right away; Cancel (or closing) puts the old one back
    private readonly Themes.AppTheme _originalTheme;
    [ObservableProperty] private int _themeIndex;
    public IReadOnlyList<string> ThemeNames { get; } = Themes.AppTheme.All.Select(t => t.Name).ToList();
    partial void OnThemeIndexChanged(int value) => Themes.AppTheme.All[Math.Clamp(value, 0, Themes.AppTheme.All.Count - 1)].Apply();

    /// <summary>Called when the window closes without Save.</summary>
    public void RevertPreview() { if (!Saved) _originalTheme.Apply(); }

    // ---- anonymous usage stats
    private readonly bool _usageWasAsked;
    [ObservableProperty] private bool _usageStatsOn;

    /// <summary>Set by the main window: runs an update check now and returns a sentence about it.</summary>
    public Func<Task<string>>? CheckUpdatesNow { get; set; }
    public string VersionText => $"You're on MM Launcher v{MMLauncher.App.Updates.SelfUpdater.CurrentVersion}.";

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (CheckUpdatesNow == null) return;
        UpdateStatus = "Checking…";
        UpdateStatus = await CheckUpdatesNow();
    }

    // What detection makes of them
    [ObservableProperty] private string _steamStatus = "";
    [ObservableProperty] private bool _steamOk;
    [ObservableProperty] private string _dayZStatus = "";
    [ObservableProperty] private bool _dayZOk;
    [ObservableProperty] private string _workshopStatus = "";
    [ObservableProperty] private bool _workshopOk;
    [ObservableProperty] private string _maintenanceMessage = "";

    public string[] LaunchMethods { get; } =
    [
        "Direct: start DayZ through BattlEye (recommended)",
        "Steam: ask Steam to start DayZ",
    ];

    public string SteamPlaceholder => "Automatic";
    public string LaunchMethodHelp => LaunchMethodIndex == 0
        ? "Runs DayZ_BE.exe from your DayZ folder, which starts BattlEye and then the game."
        : "Asks Steam to start DayZ. On some PCs this starts the game without BattlEye, and BattlEye servers then kick you (0x000400F0).";

    partial void OnSteamPathChanged(string value) => Recheck();
    partial void OnDayZPathChanged(string value) => Recheck();
    partial void OnWorkshopPathChanged(string value) => Recheck();
    partial void OnLaunchMethodIndexChanged(int value) => OnPropertyChanged(nameof(LaunchMethodHelp));

    /// <summary>Runs detection with the values in the boxes and describes the result.</summary>
    private void Recheck()
    {
        LocalSteam? found = null;
        try { found = LocalSteam.Detect(SteamPath, DayZPath, WorkshopPath); } catch { }

        string Src(bool manual) => manual ? "Using" : "Found automatically:";

        // Steam
        if (found == null || string.IsNullOrEmpty(found.SteamPath))
        {
            SteamOk = false;
            SteamStatus = string.IsNullOrWhiteSpace(SteamPath)
                ? "✗ Steam wasn't found automatically. Choose the Steam folder (the one with steam.exe)."
                : "✗ That folder doesn't exist.";
        }
        else
        {
            bool exe = File.Exists(Path.Combine(found.SteamPath, "steam.exe")) || !OperatingSystem.IsWindows();
            SteamOk = exe;
            SteamStatus = exe
                ? $"✓ {Src(found.SteamIsManual)} {found.SteamPath}  ·  {found.Libraries.Count} Steam librar{(found.Libraries.Count == 1 ? "y" : "ies")}"
                : $"✗ No steam.exe in {found.SteamPath}";
        }

        // DayZ
        var dayz = found?.DayZPath ?? (string.IsNullOrWhiteSpace(DayZPath) ? null : DayZPath.Trim());
        if (dayz == null)
        {
            DayZOk = false;
            DayZStatus = "✗ DayZ wasn't found in any Steam library. Choose the DayZ folder (the one with DayZ_BE.exe).";
        }
        else if (!LocalSteam.LooksLikeDayZ(dayz))
        {
            DayZOk = false;
            DayZStatus = $"✗ No DayZ_BE.exe or DayZ_x64.exe in {dayz}";
        }
        else
        {
            DayZOk = true;
            DayZStatus = $"✓ {Src(found?.DayZIsManual == true)} {dayz}";
        }

        // Workshop
        if (found?.WorkshopPath == null || !Directory.Exists(found.WorkshopPath))
        {
            WorkshopOk = false;
            WorkshopStatus = found?.WorkshopPath != null
                ? $"✗ {found.WorkshopPath} doesn't exist yet. It's created when Steam downloads your first DayZ mod."
                : "✗ No DayZ workshop folder found. Choose the folder that holds the numbered mod folders (…\\workshop\\content\\221100).";
        }
        else
        {
            WorkshopOk = true;
            WorkshopStatus = $"✓ {Src(found.WorkshopIsManual)} {found.WorkshopPath}  ·  {found.Mods.Count} mods downloaded";
        }
    }

    [RelayCommand]
    private async Task BrowseAsync(string which)
    {
        if (PickFolder == null) return;
        var (title, current) = which switch
        {
            "steam" => ("Choose your Steam folder (contains steam.exe)", SteamPath),
            "dayz" => ("Choose your DayZ folder (contains DayZ_BE.exe)", DayZPath),
            _ => ("Choose the DayZ workshop folder (…\\steamapps\\workshop\\content\\221100)", WorkshopPath),
        };
        var picked = await PickFolder(title, string.IsNullOrWhiteSpace(current) ? null : current);
        if (picked == null) return;
        switch (which)
        {
            case "steam": SteamPath = picked; break;
            case "dayz": DayZPath = picked; break;
            default: WorkshopPath = picked; break;
        }
    }

    [RelayCommand]
    private void Automatic(string which)
    {
        switch (which)
        {
            case "steam": SteamPath = ""; break;
            case "dayz": DayZPath = ""; break;
            default: WorkshopPath = ""; break;
        }
    }

    [RelayCommand]
    private void DetectAgain() => Recheck();

    [RelayCommand]
    private void RebuildModLinks()
    {
        try
        {
            var found = LocalSteam.Detect(SteamPath, DayZPath, WorkshopPath);
            if (found?.DayZPath == null) { MaintenanceMessage = "DayZ folder not found."; return; }
            int n = ModHealth.RemoveModLinks(found.DayZPath);
            MaintenanceMessage = $"Removed {n} mod link(s). They'll be created again the next time you join.";
        }
        catch (Exception ex)
        {
            MaintenanceMessage = "Couldn't remove the links: " + ex.Message + " (Is DayZ running?)";
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => Open(LauncherSettings.DataFolder);

    [RelayCommand]
    private void OpenLogFile() => Open(Log.FilePath);

    private void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MaintenanceMessage = "Couldn't open it: " + ex.Message; }
    }

    [RelayCommand]
    private void Save()
    {
        var s = LauncherSettings.Load(); // start from the file so hand-edited options survive
        s.SteamPathOverride = SteamPath.Trim();
        s.DayZPathOverride = DayZPath.Trim();
        s.WorkshopPathOverride = WorkshopPath.Trim();
        s.LaunchMethod = LaunchMethodIndex == 0 ? "Direct" : "Steam";
        s.PlayerName = Core.Launch.PlayerNames.Clean(PlayerName);
        s.ExtraLaunchParameters = ExtraParameters.Trim();
        s.DisplayMode = DisplayModeIndex switch { 1 => Core.Launch.GameDisplay.Fullscreen, 2 => Core.Launch.GameDisplay.Windowed, _ => Core.Launch.GameDisplay.Keep };
        s.WindowWidth = (int)Math.Clamp(WindowWidth ?? 1920, 640, 7680);
        s.WindowHeight = (int)Math.Clamp(WindowHeight ?? 1080, 360, 4320);
        s.AutoRefreshSeconds = (int)Math.Clamp(AutoRefreshSeconds ?? 30, 0, 3600);
        if (s.AutoRefreshSeconds is > 0 and < 10) s.AutoRefreshSeconds = 10;
        s.CheckForUpdates = CheckForUpdates;
        s.Theme = Themes.AppTheme.All[Math.Clamp(ThemeIndex, 0, Themes.AppTheme.All.Count - 1)].Key;
        // Still "ask" and the box wasn't touched: keep asking on the next start.
        if (_usageWasAsked || UsageStatsOn) s.SetUsageStats(UsageStatsOn);
        s.Save();
        Log.Info($"Settings saved: Steam='{s.SteamPathOverride}' DayZ='{s.DayZPathOverride}' Workshop='{s.WorkshopPathOverride}' Launch={s.LaunchMethod}");
        Saved = true;
        Close?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() { RevertPreview(); Close?.Invoke(false); }

    [RelayCommand]
    private void ResetLaunchOptions()
    {
        var d = new LauncherSettings();
        LaunchMethodIndex = d.LaunchDirect ? 0 : 1;
        PlayerName = d.PlayerName;
        ExtraParameters = d.ExtraLaunchParameters;
        DisplayModeIndex = 0;
        AutoRefreshSeconds = d.AutoRefreshSeconds;
    }
}
