using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.App.Steam;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.ViewModels;

/// <summary>JOIN SERVER, Update mods, Verify files and Repair: get mods right, then start DayZ.</summary>
public partial class MainViewModel
{
    private readonly IModDownloader _downloader = ModDownloader.Create();
    private CancellationTokenSource? _syncCts;
    private bool _skipHealthCheckOnce;

    /// <summary>Set by the window: asks for a server password, null if cancelled.</summary>
    public Func<string, Task<string?>>? AskPassword { get; set; }

    /// <summary>Set by the window: (title, message, ok button text) → true if confirmed.</summary>
    public Func<string, string, string, Task<bool>>? AskConfirm { get; set; }

    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _syncTitle = "";
    [ObservableProperty] private string _syncDetail = "";
    [ObservableProperty] private double _syncFraction;
    [ObservableProperty] private bool _syncIndeterminate;
    [ObservableProperty] private bool _syncFailed;
    [ObservableProperty] private bool _syncSucceeded;
    /// <summary>Shown in orange instead of red: nothing is broken, but something needs doing.</summary>
    [ObservableProperty] private bool _syncWarning;

    // Optional buttons on the result panel (e.g. "Repair 3 mods", "Join anyway").
    [ObservableProperty] private string _panelActionText = "";
    [ObservableProperty] private IAsyncRelayCommand? _panelAction;
    [ObservableProperty] private string _panelAction2Text = "";
    [ObservableProperty] private IAsyncRelayCommand? _panelAction2;

    public bool ShowSyncPanel => IsSyncing || SyncFailed || SyncSucceeded;
    public bool CanJoin => !IsSyncing && SteamFound && SelectedServer is { Online: true, GamePort: not null } s && (s.HasMods || s.ModsKnown);
    public bool CanUpdateMods => !IsSyncing && SteamFound && SelectedServer is { HasMods: true };
    public bool HasPanelAction => PanelAction != null && !IsSyncing;
    public bool HasPanelAction2 => PanelAction2 != null && !IsSyncing;

    partial void OnIsSyncingChanged(bool value) => RaiseJoinState();
    partial void OnSyncFailedChanged(bool value) => OnPropertyChanged(nameof(ShowSyncPanel));
    partial void OnSyncSucceededChanged(bool value) => OnPropertyChanged(nameof(ShowSyncPanel));
    partial void OnPanelActionChanged(IAsyncRelayCommand? value) => OnPropertyChanged(nameof(HasPanelAction));
    partial void OnPanelAction2Changed(IAsyncRelayCommand? value) => OnPropertyChanged(nameof(HasPanelAction2));

    private void RaiseJoinState()
    {
        OnPropertyChanged(nameof(ShowSyncPanel));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(CanUpdateMods));
        OnPropertyChanged(nameof(CanStartEditor));
        OnPropertyChanged(nameof(HasPanelAction));
        OnPropertyChanged(nameof(HasPanelAction2));
    }

    // ------------------------------------------------------------------ Join

    [RelayCommand]
    private async Task JoinAsync()
    {
        var server = SelectedServer;
        if (server == null || !CanJoin) return;
        bool auto = _rejoinJoining;
        if (!auto) { StopRejoin(); _quickExits = 0; }
        if (!auto && server.MinutesToRestart is <= 5 and > -10 && AskConfirm != null &&
            !await AskConfirm("Restart coming up",
                $"{server.Name} restarts {server.RestartText}. You'll be kicked when it does.\n\nTip: tick \"Rejoin automatically if DayZ closes\" and the launcher brings you back once it's up again.",
                "Join anyway"))
            return;
        Log.Info($"{(auto ? "Auto-rejoin" : "Join requested")}: {server.Name} ({server.ConnectText})");

        var mods = await PrepareModsAsync(server, askSteamAnyway: false);
        if (mods == null) return;

        // Quick local file check before launching: a damaged mod usually means a kick or crash.
        if (!_skipHealthCheckOnce)
        {
            var damaged = CheckHealth(server);
            if (damaged.Count > 0)
            {
                ShowResult(false, $"{damaged.Count} mod(s) look damaged",
                    $"{Names(damaged)}. Repairing downloads them fresh through Steam. You can also join anyway.");
                SetPanelActions(
                    ($"Repair {damaged.Count} mod(s)", () => RepairModsAsync(server, damaged, confirm: false, joinAfter: true)),
                    ("Join anyway", () => { _skipHealthCheckOnce = true; return JoinAsync(); }));
                return;
            }
        }
        _skipHealthCheckOnce = false;

        string? password = null;
        if (server.Passworded)
        {
            password = auto && _rejoinServerKey == server.Key && _rejoinPassword != null
                ? _rejoinPassword
                : AskPassword == null ? null : await AskPassword(server.Name);
            if (password == null) { ShowResult(false, "Join cancelled", "No password entered."); return; }
        }

        try
        {
            if (GameLauncher.DayZIsRunning())
                throw new LaunchException("DayZ is already running. Close it first, then click JOIN SERVER again.");
            var command = GameLauncher.Build(new LaunchRequest(server.ConnectIp, server.GamePort!.Value, mods, password), _service.Settings, _steam!);
            if (!GameLauncher.SteamIsRunning())
                throw new LaunchException("Steam isn't running. Start Steam, then click JOIN SERVER again.");
            GameLauncher.Start(command);

            // Don't assume it worked: wait for the DayZ process to actually appear.
            ClearPanel();
            IsSyncing = true;
            SyncIndeterminate = true;
            SyncTitle = "Starting DayZ…";
            SyncDetail = $"Joining {server.Name} with {mods.Count} mods.";
            bool started = GameLauncher.DryRun || await GameLauncher.WaitForDayZAsync(TimeSpan.FromSeconds(45));
            IsSyncing = false;

            if (started)
            {
                Log.Info("DayZ process detected.");
                ShowResult(true, "DayZ is starting", $"Joining {server.Name} with {mods.Count} mods. Good hunting.");
                StatusMessage = "DayZ launched";
                StartRejoinWatch(server, password);
                RememberRecent(server);
            }
            else
            {
                var hint = _service.Settings.LaunchDirect
                    ? "If a Windows prompt asked to let BattlEye make changes, accept it and try again. Otherwise try the \"Steam\" launch method in Settings."
                    : "If Steam shows a pop-up asking to allow the launch options, accept it. Otherwise try the \"Direct\" launch method in Settings.";
                Log.Error("DayZ process did not appear within 45 seconds.");
                ShowResult(false, "DayZ didn't start", "The start command was sent but DayZ didn't open within 45 seconds. " + hint);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Launch failed: " + ex.Message);
            ShowResult(false, "Couldn't start DayZ", ex.Message);
        }
    }

    [RelayCommand]
    private async Task UpdateModsAsync()
    {
        var server = SelectedServer;
        if (server == null || !CanUpdateMods) return;
        Log.Info($"Update mods requested: {server.Name}");
        var mods = await PrepareModsAsync(server, askSteamAnyway: true);
        if (mods != null)
            ShowResult(true, "Mods ready", $"All {mods.Count} mods for {server.Name} are downloaded and up to date.");
    }

    // ------------------------------------------------------------------ Verify + Repair

    [RelayCommand]
    private async Task VerifyFilesAsync()
    {
        var server = SelectedServer;
        if (server == null || IsSyncing) return;
        DetectSteam();

        // Versions too, not just files: fetch fresh Workshop details so an update published
        // a few minutes ago shows up.
        var workshop = await SafeWorkshopAsync(server);
        server.ApplyModDetails(workshop, _steam);

        var damaged = CheckHealth(server);
        var outdated = server.Mods.Where(m => m.IsUpdate).ToList();
        var notDownloaded = server.Mods.Where(m => m.IsMissing && !m.IsDamaged && m.WorkshopId != 0).ToList();
        int onDisk = server.Mods.Count(m => _steam?.Mods.ContainsKey(m.WorkshopId) == true);
        Log.Info($"Verify {server.Name}: {onDisk} on disk, {damaged.Count} damaged, {outdated.Count} outdated, {notDownloaded.Count} not downloaded.");

        if (damaged.Count > 0)
        {
            ShowResult(false, $"{damaged.Count} mod(s) look damaged", Names(damaged) + ". Hover a red \"Damaged\" badge for details." +
                (outdated.Count + notDownloaded.Count > 0 ? $" Also {outdated.Count + notDownloaded.Count} mod(s) need downloading or updating." : ""));
            SetPanelActions(($"Repair {damaged.Count} mod(s)", () => RepairModsAsync(server, damaged, confirm: false, joinAfter: false)), null);
            return;
        }

        if (outdated.Count + notDownloaded.Count > 0)
        {
            var parts = new List<string>();
            if (outdated.Count > 0) parts.Add($"{outdated.Count} {(outdated.Count == 1 ? "has" : "have")} a newer version on the Workshop: {Names(outdated)}");
            if (notDownloaded.Count > 0) parts.Add($"{notDownloaded.Count} {(notDownloaded.Count == 1 ? "isn't" : "aren't")} downloaded yet: {Names(notDownloaded)}");
            ShowResult(false, "Files are fine, but mods need updating",
                $"Checked {onDisk} downloaded mod(s): no damaged files. {string.Join(". ", parts)}.");
            SyncWarning = true;
            SetPanelActions(($"Update {outdated.Count + notDownloaded.Count} mod(s)", UpdateModsAsync), null);
            return;
        }

        ShowResult(true, "Mods are up to date and look good",
            $"Checked {onDisk} mod(s): all match the latest Workshop version, and their folders, .pbo files and signatures are in place.");
        SetPanelActions(("Repair all mods anyway", () => RepairAllAsync()), null);
    }

    [RelayCommand]
    private Task RepairModAsync(ModViewModel? mod)
    {
        var server = SelectedServer;
        if (server == null || mod == null || !mod.CanRepair) return Task.CompletedTask;
        return RepairModsAsync(server, [mod], confirm: true, joinAfter: false);
    }

    [RelayCommand]
    private Task RepairAllAsync()
    {
        var server = SelectedServer;
        if (server == null) return Task.CompletedTask;
        return RepairModsAsync(server, server.Mods.Where(m => m.CanRepair).ToList(), confirm: true, joinAfter: false);
    }

    /// <summary>Deletes the given mods' folders and has Steam download them fresh.</summary>
    private async Task RepairModsAsync(ServerViewModel server, IReadOnlyList<ModViewModel> mods, bool confirm, bool joinAfter)
    {
        if (IsSyncing || mods.Count == 0) return;
        ClearPanel();
        long bytes = mods.Sum(m => m.SizeBytes);
        if (confirm && AskConfirm != null)
        {
            var what = mods.Count == 1 ? mods[0].Name : $"{mods.Count} mods";
            bool ok = await AskConfirm($"Repair {what}?",
                $"This deletes the downloaded files for {what} and downloads them again through Steam " +
                $"(about {ModDownloader.Size(bytes)}). Nothing else is touched.",
                "Repair");
            if (!ok) return;
        }

        DetectSteam();
        if (_steam?.WorkshopPath == null) { ShowResult(false, "Can't repair", "The workshop folder wasn't found. Check Settings."); return; }
        if (GameLauncher.DayZIsRunning()) { ShowResult(false, "DayZ is running", "Close DayZ before repairing mods."); return; }

        Log.Info($"Repair requested for {server.Name}: {string.Join(", ", mods.Select(m => $"{m.WorkshopId} {m.Name}"))}");

        // 1) Move each mod's folder aside. Each move is all-or-nothing, so a locked file
        //    leaves that one mod untouched; the rest carry on.
        var moved = new Dictionary<ulong, string?>();   // id -> old copy (null = nothing was there)
        var blocked = new List<(ModViewModel Mod, string Why)>();
        foreach (var m in mods)
        {
            try
            {
                moved[m.WorkshopId] = ModHealth.MoveAsideForRepair(_steam, m.WorkshopId, m.Name);
            }
            catch (Exception ex)
            {
                Log.Warn($"Repair skipped {m.WorkshopId} {m.Name}: {ex.Message}");
                blocked.Add((m, ex.Message));
            }
        }
        if (moved.Count == 0)
        {
            ShowResult(false, "Couldn't repair", blocked.Count == 1 ? Cap(blocked[0].Why) : $"Every selected mod had a file open in another program. First one: {blocked[0].Why}");
            return;
        }
        foreach (var m in mods) if (moved.ContainsKey(m.WorkshopId)) m.HealthProblems = "";

        // 2) Steam downloads fresh copies.
        var ids = moved.Keys.ToList();
        var result = await PrepareModsAsync(server, askSteamAnyway: true, new RepairSpec(_steam.WorkshopPath, ids));

        // 3) Anything that didn't come back gets its old copy restored, so a failed or
        //    cancelled repair never leaves you with less than you had. Then clear the trash.
        DetectSteam();
        int restored = 0;
        if (_steam != null)
        {
            foreach (var (id, oldCopy) in moved)
                if (oldCopy != null && ModHealth.RestoreIfMissing(_steam, id, oldCopy)) restored++;
            ModHealth.PurgeTrash(_steam);
            DetectSteam();
            foreach (var s in Servers) s.ApplyModDetails(await SafeWorkshopAsync(server), _steam);
        }

        string skippedNote = blocked.Count == 0 ? "" :
            $" Skipped {blocked.Count} mod(s) with files open in another program: {string.Join("; ", blocked.Take(3).Select(b => b.Why))}";

        if (result == null)
        {
            if (restored > 0)
                ShowResult(false, "Repair didn't finish", $"{SyncDetail} Put back the previous copy of {restored} mod(s), so nothing was lost.{skippedNote}");
            return;
        }

        // Re-check what we repaired: still bad after a fresh download = the upload itself.
        var stillBad = CheckHealth(server).Where(m => ids.Contains(m.WorkshopId)).ToList();
        if (stillBad.Count > 0)
        {
            ShowResult(false, "Downloaded again, but still has problems",
                $"{Names(stillBad)}. A fresh copy from Steam has the same problem, so it's most likely in the mod's Workshop upload. Let staff know.{skippedNote}");
            return;
        }

        if (blocked.Count > 0)
        {
            ShowResult(false, $"Repaired {ids.Count} mod(s), skipped {blocked.Count}",
                $"Skipped because a file was open in another program: {string.Join("; ", blocked.Take(3).Select(b => b.Why))}");
            SetPanelActions(($"Try the {blocked.Count} skipped again", () => RepairModsAsync(server, blocked.Select(b => b.Mod).ToList(), confirm: false, joinAfter: false)), null);
            return;
        }

        if (joinAfter) { await JoinAsync(); return; }
        ShowResult(true, "Repair complete", $"{(ids.Count == 1 ? mods[0].Name : $"{ids.Count} mods")} downloaded fresh and checked.");
    }

    private static string Cap(string s) => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s;

    private async Task<IReadOnlyDictionary<ulong, WorkshopItem>> SafeWorkshopAsync(ServerViewModel server)
    {
        try { return await _service.Workshop.GetDetailsAsync(server.Mods.Where(m => m.WorkshopId != 0).Select(m => m.WorkshopId)); }
        catch { return new Dictionary<ulong, WorkshopItem>(); }
    }

    /// <summary>Runs the file check on every downloaded mod of a server; returns the damaged ones.</summary>
    private List<ModViewModel> CheckHealth(ServerViewModel server)
    {
        var damaged = new List<ModViewModel>();
        foreach (var m in server.Mods)
        {
            if (m.WorkshopId == 0 || _steam == null || !_steam.Mods.TryGetValue(m.WorkshopId, out var installed))
            {
                m.HealthProblems = "";
                continue;
            }
            var report = ModHealth.Check(m.WorkshopId, installed);
            m.HealthProblems = report.Ok ? "" : report.Summary;
            if (!report.Ok)
            {
                damaged.Add(m);
                Log.Warn($"File check {m.WorkshopId} {m.Name}: {report.Summary}");
            }
        }
        server.UpdateModSummary();
        return damaged;
    }

    // ------------------------------------------------------------------ Panel

    [RelayCommand]
    private void CancelSync() => _syncCts?.Cancel();

    [RelayCommand]
    private void DismissSync() => ClearPanel();

    [RelayCommand]
    private void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = "Couldn't open the log: " + ex.Message; }
    }

    private void ClearPanel()
    {
        SyncFailed = false;
        SyncSucceeded = false;
        SyncWarning = false;
        SetPanelActions(null, null);
    }

    private void SetPanelActions((string Text, Func<Task> Run)? first, (string Text, Func<Task> Run)? second)
    {
        PanelActionText = first?.Text ?? "";
        PanelAction = first is { } f ? new AsyncRelayCommand(f.Run) : null;
        PanelAction2Text = second?.Text ?? "";
        PanelAction2 = second is { } s ? new AsyncRelayCommand(s.Run) : null;
    }

    private static string Names(IReadOnlyList<ModViewModel> mods) =>
        string.Join(", ", mods.Take(5).Select(m => m.Name)) + (mods.Count > 5 ? $" and {mods.Count - 5} more" : "");

    // ------------------------------------------------------------------ Download

    /// <summary>
    /// Makes sure every mod the server needs is downloaded and current. Returns the mod list
    /// in load order when ready, or null (after showing why) when not.
    /// </summary>
    private Task<List<RequiredMod>?> PrepareModsAsync(ServerViewModel server, bool askSteamAnyway, RepairSpec? repair = null) =>
        PrepareModListAsync(server.Name, server.Mods.Select(m => new RequiredMod(m.WorkshopId, m.Name)).ToList(), askSteamAnyway, repair);

    /// <summary>
    /// Makes sure every mod in a list (a server's, or a DayZ Editor preset's) is downloaded and
    /// current. Returns the list in load order when ready, or null (after showing why) when not.
    /// </summary>
    private async Task<List<RequiredMod>?> PrepareModListAsync(string label, List<RequiredMod> mods, bool askSteamAnyway, RepairSpec? repair = null)
    {
        ClearPanel();
        _service.ReloadSettings(); // pick up any edits to the launch options
        DetectSteam();

        if (_steam == null) { ShowResult(false, "Steam not found", "Install Steam, or choose your folders in Settings."); return null; }
        if (_steam.DayZPath == null) { ShowResult(false, "DayZ not found", "Install DayZ in Steam, or choose its folder in Settings."); return null; }

        // DistinctBy: a mod listed twice must only be downloaded and loaded once.
        var required = mods.DistinctBy(m => m.Id).ToList();
        IReadOnlyDictionary<ulong, WorkshopItem> workshop = new Dictionary<ulong, WorkshopItem>();
        try { workshop = await _service.Workshop.GetDetailsAsync(required.Where(m => m.Id != 0).Select(m => m.Id)); }
        catch (Exception ex) { Log.Warn("Workshop lookup failed before sync: " + ex.Message); }

        var plan = ModPlan.Build(required, _steam, workshop);
        Log.Info($"Plan for {label}: {required.Count} mods, {plan.ToDownload.Count} to download ({ModDownloader.Size(plan.DownloadBytes)}), {plan.Blocked.Count} blocked.");

        if (plan.Blocked.Count > 0)
        {
            var names = string.Join(", ", plan.Blocked.Select(b => b.Id == 0 ? $"{b.Name} (no workshop ID)" : $"{b.Name} (removed from the Workshop)"));
            ShowResult(false, "Some mods can't be downloaded", names + ". Let staff know.");
            return null;
        }

        if (plan.ToDownload.Count == 0 && !askSteamAnyway)
            return required;

        if (GameLauncher.DayZIsRunning())
        {
            ShowResult(false, "DayZ is running", "Close DayZ before downloading mods, then try again.");
            return null;
        }

        var sizes = required.ToDictionary(m => m.Id, m => workshop.GetValueOrDefault(m.Id)?.SizeBytes ?? 0);
        _syncCts = new CancellationTokenSource();
        IsSyncing = true;
        SyncTitle = repair != null
            ? $"Repairing {repair.Ids.Count} mod(s)"
            : plan.ToDownload.Count > 0
                ? $"Getting {plan.ToDownload.Count} mod(s), about {ModDownloader.Size(plan.DownloadBytes)}"
                : "Checking mods with Steam";
        SyncDetail = "";
        SyncIndeterminate = true;

        var progress = new Progress<SyncProgress>(p =>
        {
            // Progress reports arrive asynchronously; drop any that land after the sync
            // ended so they can't overwrite the final result message.
            if (!IsSyncing) return;
            SyncTitle = p.Title;
            SyncDetail = p.Detail;
            SyncIndeterminate = p.Fraction < 0;
            if (p.Fraction >= 0) SyncFraction = p.Fraction;
        });

        SyncResult result;
        try
        {
            // Always tell the downloader where mods live, so it checks folders on disk and
            // never trusts Steam's "installed" flag alone (it goes stale if folders are deleted).
            var spec = repair ?? new RepairSpec(_steam.WorkshopPath ?? "", []);
            result = await _downloader.SyncAsync(required, plan.ToDownload, sizes, spec, progress, _syncCts.Token);
        }
        catch (Exception ex)
        {
            Log.Error("Sync crashed: " + ex);
            result = new SyncResult(false, "Something went wrong talking to Steam: " + ex.Message, plan.ToDownload);
        }
        finally
        {
            IsSyncing = false;
        }

        // Re-read what's on disk and refresh every mod badge (servers and the editor preset).
        DetectSteam();
        foreach (var s in Servers) s.ApplyModDetails(workshop, _steam);
        RefreshEditorModStates(workshop);
        Log.Info("Sync result: " + result.Message);

        if (!result.Success)
        {
            ShowResult(false, repair != null ? "Repair didn't finish" : "Mods aren't ready", result.Message);
            return null;
        }

        // Steam said everything finished; just make sure each folder is really there.
        var missing = required.Where(m => _steam?.Mods.ContainsKey(m.Id) != true).ToList();
        if (missing.Count > 0)
        {
            ShowResult(false, "Mods aren't ready",
                $"Steam finished, but {missing.Count} mod folder(s) aren't on disk yet: {string.Join(", ", missing.Take(5).Select(m => m.Name))}. Wait a moment and try again.");
            return null;
        }
        return required;
    }

    private void ShowResult(bool ok, string title, string detail)
    {
        SyncTitle = title;
        SyncDetail = detail;
        SyncSucceeded = ok;
        SyncFailed = !ok;
        SyncWarning = false;
        if (!ok) Log.Warn($"{title}: {detail}");
    }
}
