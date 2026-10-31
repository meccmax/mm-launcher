using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// Auto-rejoin: after the launcher starts DayZ, watch it. When DayZ closes (a crash, or the
/// player quit to update mods), wait for the server to be up, then join again. If the server
/// went down (restart), wait a random extra minute or two so not everyone hits it at once.
/// The countdown can always be cancelled.
/// </summary>
public partial class MainViewModel
{
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ServerCheckEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GiveUpWaitingAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QuickExit = TimeSpan.FromSeconds(90);
    private const int CountdownServerUp = 15;          // seconds: crash / quit while the server stayed up
    private const int MinAfterRestart = 60, MaxAfterRestart = 120; // seconds, random per player
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

    public bool AutoRejoin
    {
        get => _service.Settings.AutoRejoin;
        set
        {
            if (_service.Settings.AutoRejoin == value) return;
            _service.Settings.AutoRejoin = value;
            try { _service.Settings.Save(); } catch (Exception ex) { Log.Warn("Couldn't save settings: " + ex.Message); }
            OnPropertyChanged();
            Log.Info($"Auto-rejoin {(value ? "on" : "off")}.");
            if (!value && RejoinActive) StopRejoin("Auto-rejoin turned off.");
        }
    }

    /// <summary>Called after DayZ was started for <paramref name="server"/>.</summary>
    private void StartRejoinWatch(ServerViewModel server, string? password)
    {
        _rejoinCts?.Cancel();
        _rejoinCts = new CancellationTokenSource();
        _rejoinServerKey = server.Key;
        _rejoinPassword = password;
        var ct = _rejoinCts.Token;
        _ = WatchAsync(server, DateTime.UtcNow, ct);
    }

    /// <summary>A manual join (or turning the option off) ends any pending rejoin.</summary>
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
        Log.Info("Auto-rejoin cancelled by player.");
        StopRejoin("Auto-rejoin cancelled.");
    }

    private async Task WatchAsync(ServerViewModel watched, DateTime launchedAt, CancellationToken ct)
    {
        string key = watched.Key, name = watched.Name;
        // MM servers are rebuilt on every list reload, so look them up by key; others are kept as is.
        ServerViewModel Current() => Servers.FirstOrDefault(s => s.Key == key) ?? watched;
        try
        {
            // 1) Wait for DayZ to close. Two misses in a row, so the hand-over from
            //    DayZ_BE.exe to DayZ_x64.exe is never mistaken for an exit.
            int misses = 0;
            while (misses < 2)
            {
                await Task.Delay(WatchEvery, ct);
                misses = GameLauncher.DayZIsRunning() ? 0 : misses + 1;
            }
            var ranFor = DateTime.UtcNow - launchedAt;
            Log.Info($"DayZ closed after {ranFor.TotalMinutes:0.0} min.");
            if (!AutoRejoin) return;

            // A game that dies right after starting would otherwise loop forever.
            _quickExits = ranFor < QuickExit ? _quickExits + 1 : 0;
            if (_quickExits >= MaxQuickExits)
            {
                _quickExits = 0;
                Log.Warn("DayZ closed right after starting several times; auto-rejoin paused.");
                ShowResult(false, "Auto-rejoin paused",
                    "DayZ closed right after starting several times in a row. Use \"Report a problem\" to send the logs to staff, then JOIN SERVER when you're ready.");
                return;
            }

            BringToFront?.Invoke();
            if (!IsSyncing) ClearPanel(); // the old "DayZ is starting" message
            RejoinActive = true;
            RejoinTitle = $"DayZ closed. Rejoining {name}…";

            // 2) Wait until the server is back and has its mod list. If a scheduled restart is
            //    due right now, wait for it to actually happen instead of joining a server
            //    that's about to go down.
            bool expectRestart = Current().MinutesToRestart is < 5 and > -10;
            if (expectRestart) Log.Info($"{name} has a restart due; waiting for it before rejoining.");
            bool wentDown = false;
            var waitingSince = DateTime.UtcNow;
            ServerViewModel? server;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (GameLauncher.DayZIsRunning()) { Log.Info("DayZ was started again by hand; auto-rejoin stood down."); StopRejoin(); return; }
                if (!IsBusy && !IsSyncing)
                {
                    if (Servers.Any(s => s.Key == key)) await RefreshAsync();
                    else await RefreshOneAsync(watched);
                }
                server = Current();
                // The agent's uptime shows a restart even if we never caught the server offline.
                bool restarted = server is { ModsFromAgent: true, UptimeSeconds: > 0 } &&
                                 server.UptimeSeconds < (DateTime.UtcNow - launchedAt).TotalSeconds;
                if (server is { Online: false } || restarted) wentDown = true;
                bool restartDone = !expectRestart || wentDown || DateTime.UtcNow - waitingSince > TimeSpan.FromMinutes(15);
                if (server is { Online: true, GamePort: not null } && (server.HasMods || server.ModsKnown) && restartDone) break;
                if (DateTime.UtcNow - waitingSince > GiveUpWaitingAfter)
                {
                    RejoinActive = false;
                    ShowResult(false, "Auto-rejoin stopped", $"{name} didn't come back within {GiveUpWaitingAfter.TotalMinutes:0} minutes.");
                    return;
                }
                RejoinDetail = server is { Online: true } && expectRestart && !wentDown
                    ? "A scheduled restart is due. Waiting for it to happen…"
                    : "The server is restarting. Waiting for it to come back online…";
                await Task.Delay(ServerCheckEvery, ct);
            }

            // 3) Countdown. After a restart, a random wait spreads players out.
            int seconds = wentDown ? Random.Shared.Next(MinAfterRestart, MaxAfterRestart + 1) : CountdownServerUp;
            Log.Info($"{name} is up (went down: {wentDown}); rejoining in {seconds}s.");
            for (int left = seconds; left > 0; left--)
            {
                RejoinDetail = wentDown
                    ? $"The server is back. Rejoining in {left}s (a short random wait so everyone doesn't connect at once)."
                    : $"Updating mods and rejoining in {left}s.";
                await Task.Delay(1000, ct);
                if (GameLauncher.DayZIsRunning()) { Log.Info("DayZ was started again by hand; auto-rejoin stood down."); StopRejoin(); return; }
            }

            // 4) Join like the button would, with the remembered password.
            while (IsBusy || IsSyncing) await Task.Delay(1000, ct);
            server = Current();
            if (server == null) { RejoinActive = false; ShowResult(false, "Auto-rejoin stopped", $"{name} isn't in the server list anymore."); return; }
            RejoinActive = false;
            SelectedServer = server;
            _rejoinJoining = true;
            try { await JoinAsync(); }
            finally { _rejoinJoining = false; }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the player, a manual join, or turning the option off.
        }
        catch (Exception ex)
        {
            Log.Error("Auto-rejoin failed: " + ex);
            RejoinActive = false;
        }
    }
}
