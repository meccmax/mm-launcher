using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// Two ways back onto a server, both cancellable at any point:
///  - "Rejoin automatically if DayZ crashes" (checkbox): after the launcher starts DayZ it
///    watches the game; if DayZ CRASHES it waits for the server and joins again. A normal
///    quit never rejoins (players log off on purpose).
///  - "JOIN WHEN READY" (opt-in, each time): when a server is restarting or down, the player
///    queues a join; the launcher waits for the server to come back, updates mods and starts
///    DayZ. Also offered after a normal quit if the server is restarting.
/// After a restart a short random wait spreads players out so they don't all hit the
/// server in the same second.
/// </summary>
public partial class MainViewModel
{
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ServerCheckEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GiveUpWaitingAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QuickExit = TimeSpan.FromSeconds(90);
    private const int CountdownServerUp = 15;                    // seconds: crash while the server stayed up
    private const int MinAfterRestart = 30, MaxAfterRestart = 90; // seconds, random per player
    private const int MaxQuickExits = 3;

    private CancellationTokenSource? _rejoinCts;
    private string? _rejoinPassword;   // memory only, never written to disk
    private string? _rejoinServerKey;
    private bool _rejoinJoining;
    private int _quickExits;

    /// <summary>Set by the window: bring the launcher to the front.</summary>
    public Action? BringToFront { get; set; }

    [ObservableProperty] private bool _rejoinActive;
    [ObservableProperty] private string _rejoinTitle = "";
    [ObservableProperty] private string _rejoinDetail = "";

    partial void OnRejoinActiveChanged(bool value) => RaiseJoinState();

    public bool AutoRejoin
    {
        get => _service.Settings.AutoRejoin;
        set
        {
            if (_service.Settings.AutoRejoin == value) return;
            _service.Settings.AutoRejoin = value;
            try { _service.Settings.Save(); } catch (Exception ex) { Log.Warn("Couldn't save settings: " + ex.Message); }
            OnPropertyChanged();
            Log.Info($"Rejoin after a crash {(value ? "on" : "off")}.");
        }
    }

    /// <summary>The selected server is restarting or down: offer to join as soon as it's back.</summary>
    public bool CanJoinWhenReady => !IsSyncing && !RejoinActive && SteamFound &&
                                    SelectedServer is { HasStatus: true, Online: false, GamePort: not null };

    /// <summary>Called after DayZ was started for <paramref name="server"/>.</summary>
    private void StartRejoinWatch(ServerViewModel server, string? password)
    {
        _rejoinCts?.Cancel();
        _rejoinCts = new CancellationTokenSource();
        _rejoinServerKey = server.Key;
        _rejoinPassword = password;
        _ = WatchAsync(server, DateTime.UtcNow, _rejoinCts.Token);
    }

    /// <summary>A manual join ends any pending rejoin or queued join.</summary>
    private void StopRejoin(string? why = null)
    {
        _rejoinCts?.Cancel();
        _rejoinCts = null;
        if (RejoinActive && why != null) StatusMessage = why;
        RejoinActive = false;
    }

    [RelayCommand]
    private void CancelRejoin()
    {
        Log.Info("Rejoin / join-when-ready cancelled by player.");
        StopRejoin("Cancelled.");
    }

    // ------------------------------------------------------------------ join when ready

    [RelayCommand]
    private Task JoinWhenReadyAsync() => SelectedServer is { } s ? QueueJoinAsync(s) : Task.CompletedTask;

    private async Task QueueJoinAsync(ServerViewModel server)
    {
        if (RejoinActive || IsSyncing) return;
        // Still sitting in DayZ (kicked to the menu or the connection screen)? DayZ can't be
        // told to connect from outside, so the launcher closes it and starts it again,
        // connected, once the server is ready. Ask first: the player may be in a game.
        bool closeGame = GameLauncher.DayZIsRunning();
        if (closeGame && AskConfirm != null &&
            !await AskConfirm("DayZ is still open",
                $"When {server.Name} is ready, the launcher closes DayZ and starts it again, connected to the server.\n\nOnly do this if you're at DayZ's menu or connection screen, not in a game.",
                "Join when ready"))
            return;
        string? password = null;
        if (server.Passworded)
        {
            password = await GetJoinPasswordAsync(server);
            if (password == null) return;
        }
        _rejoinCts?.Cancel();
        _rejoinCts = new CancellationTokenSource();
        _rejoinServerKey = server.Key;
        _rejoinPassword = password;
        _quickExits = 0;
        ClearPanel();
        Log.Info($"Join when ready: {server.Name}");
        _ = WaitAndJoinAsync(server, DateTime.UtcNow, $"Joining {server.Name} when it's ready", wentDown: !server.Online, _rejoinCts.Token, closeGame);
    }

    // ------------------------------------------------------------------ watching DayZ

    private async Task WatchAsync(ServerViewModel watched, DateTime launchedAt, CancellationToken ct)
    {
        string key = watched.Key, name = watched.Name;
        try
        {
            // 1) Wait for DayZ to close. Two misses in a row, so the hand-over from
            //    DayZ_BE.exe to DayZ_x64.exe is never mistaken for an exit. Keep hold of
            //    DayZ_x64 so its exit code can be read afterwards.
            Process? game = null;
            int misses = 0;
            while (misses < 2)
            {
                await Task.Delay(WatchEvery, ct);
                if (game == null)
                {
                    try { game = Process.GetProcessesByName("DayZ_x64").FirstOrDefault(); _ = game?.HasExited; } // opens the handle
                    catch { game = null; }
                }
                misses = GameLauncher.DayZIsRunning() ? 0 : misses + 1;
            }
            int? exitCode = null;
            try { if (game is { HasExited: true }) exitCode = game.ExitCode; } catch { }
            game?.Dispose();

            var ranFor = DateTime.UtcNow - launchedAt;
            bool crashed = GameExit.LooksLikeCrash(exitCode, launchedAt);
            Log.Info($"DayZ closed after {ranFor.TotalMinutes:0.0} min (exit code {(exitCode?.ToString("X") ?? "unknown")}, {(crashed ? "crashed" : "normal quit")}).");
            bool mm = IsMmServer(watched);
            TrackUsage(new() { Type = "play", Mm = mm, Server = mm ? name : "", Seconds = (int)ranFor.TotalSeconds, Ok = !crashed });

            ServerViewModel Current() => Servers.FirstOrDefault(s => s.Key == key) ?? watched;

            if (!crashed)
            {
                // A normal quit: never rejoin on its own. But if the server is restarting
                // (they were probably kicked by it), offer to join once it's back.
                if (!IsBusy && !IsSyncing)
                {
                    if (Servers.Any(s => s.Key == key)) await RefreshAsync(); else await RefreshOneAsync(watched);
                }
                var now = Current();
                if (now is { Online: false } || now.MinutesToRestart is < 3 and > -10)
                {
                    if (!IsSyncing) ClearPanel();
                    ShowResult(false, $"{name} is restarting", "You can queue up: the launcher joins for you as soon as it's back (cancel any time).");
                    SyncWarning = true;
                    SetPanelActions(("Join when it's ready", () => QueueJoinAsync(Current())), null);
                }
                return;
            }

            if (!AutoRejoin)
            {
                if (!IsSyncing) ClearPanel();
                ShowResult(false, "DayZ crashed", "Use \"Report a problem\" to send the crash logs to staff. Tick \"Rejoin automatically if DayZ crashes\" to be brought back automatically next time.");
                SetPanelActions(("Rejoin", () => Current().Online ? JoinAsync() : QueueJoinAsync(Current())), ("Report a problem", ReportProblemAsync));
                return;
            }

            // A game that dies right after starting would otherwise loop forever.
            _quickExits = ranFor < QuickExit ? _quickExits + 1 : 0;
            if (_quickExits >= MaxQuickExits)
            {
                _quickExits = 0;
                Log.Warn("DayZ crashed right after starting several times; auto-rejoin paused.");
                ShowResult(false, "Auto-rejoin paused",
                    "DayZ crashed right after starting several times in a row. Use \"Report a problem\" to send the logs to staff, then JOIN SERVER when you're ready.");
                return;
            }

            BringToFront?.Invoke();
            if (!IsSyncing) ClearPanel(); // the old "DayZ is starting" message
            await WaitAndJoinAsync(Current(), launchedAt, $"DayZ crashed. Rejoining {name}…", wentDown: false, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Auto-rejoin failed: " + ex);
            RejoinActive = false;
        }
    }

    /// <summary>
    /// Waits until the server is up (and through a restart that's due right now), counts down,
    /// then joins like the button would, with the remembered password.
    /// </summary>
    private async Task WaitAndJoinAsync(ServerViewModel watched, DateTime since, string title, bool wentDown, CancellationToken ct, bool closeGame = false)
    {
        string key = watched.Key, name = watched.Name;
        ServerViewModel Current() => Servers.FirstOrDefault(s => s.Key == key) ?? watched;
        try
        {
            RejoinActive = true;
            RejoinTitle = title;
            RejoinDetail = "Checking the server…";

            bool expectRestart = !wentDown && Current().MinutesToRestart is < 5 and > -10;
            if (expectRestart) Log.Info($"{name} has a restart due; waiting for it before joining.");
            var waitingSince = DateTime.UtcNow;
            ServerViewModel server;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!closeGame && GameLauncher.DayZIsRunning()) { Log.Info("DayZ was started by hand; stood down."); StopRejoin(); return; }
                if (!IsBusy && !IsSyncing)
                {
                    if (Servers.Any(s => s.Key == key)) await RefreshAsync(); else await RefreshOneAsync(watched);
                }
                server = Current();
                // The agent's uptime shows a restart even if we never caught the server offline.
                bool restarted = server is { ModsFromAgent: true, UptimeSeconds: > 0 } &&
                                 server.UptimeSeconds < (DateTime.UtcNow - since).TotalSeconds;
                if (!server.Online || restarted) wentDown = true;
                bool restartDone = !expectRestart || wentDown || DateTime.UtcNow - waitingSince > TimeSpan.FromMinutes(15);
                if (server is { Online: true, GamePort: not null } && (server.HasMods || server.ModsKnown) && restartDone) break;
                if (DateTime.UtcNow - waitingSince > GiveUpWaitingAfter)
                {
                    RejoinActive = false;
                    ShowResult(false, "Stopped waiting", $"{name} didn't come back within {GiveUpWaitingAfter.TotalMinutes:0} minutes.");
                    return;
                }
                RejoinDetail = (server.Online && expectRestart && !wentDown
                    ? "A scheduled restart is due. Waiting for it to happen, then you'll be joined automatically…"
                    : "The server is restarting. You'll be joined automatically as soon as it's back…") +
                    (closeGame ? " (DayZ will be closed and started again, connected.)" : "");
                await Task.Delay(ServerCheckEvery, ct);
            }

            int seconds = wentDown ? Random.Shared.Next(MinAfterRestart, MaxAfterRestart + 1) : CountdownServerUp;
            Log.Info($"{name} is up (went down: {wentDown}); joining in {seconds}s.");
            for (int left = seconds; left > 0; left--)
            {
                RejoinDetail = (wentDown
                    ? $"The server is back. Joining in {left}s (a short random wait so everyone doesn't connect at once)."
                    : $"Updating mods and joining in {left}s.") + (closeGame ? " DayZ will be closed and started again." : "");
                await Task.Delay(1000, ct);
                if (!closeGame && GameLauncher.DayZIsRunning()) { Log.Info("DayZ was started by hand; stood down."); StopRejoin(); return; }
            }

            while (IsBusy || IsSyncing) await Task.Delay(1000, ct);
            if (closeGame && GameLauncher.DayZIsRunning())
            {
                RejoinDetail = "Closing DayZ so it can start again connected to the server…";
                Log.Info("Join when ready: closing DayZ to reconnect.");
                if (!await GameLauncher.CloseDayZAsync())
                {
                    RejoinActive = false;
                    ShowResult(false, "Couldn't close DayZ", "Close DayZ yourself, then click JOIN SERVER.");
                    return;
                }
            }
            RejoinActive = false;
            SelectedServer = Current();
            _rejoinJoining = true;
            try { await JoinAsync(); }
            finally { _rejoinJoining = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Join when ready failed: " + ex);
            RejoinActive = false;
        }
    }
}
