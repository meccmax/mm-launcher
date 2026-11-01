using MMLauncher.App.Updates;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Usage;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// Anonymous usage stats for MM (only when the player said yes): launcher starts, joins and
/// play time on MM servers, joins to other servers (counted only), mod downloads and errors.
/// Sent to an MM server agent over its encrypted connection; kept in usage-queue.jsonc until then.
/// </summary>
public partial class MainViewModel
{
    private UsageQueue? _usageQueue;
    private int _usageSending;

    public const string UsageQuestion =
        "Help us make MM Launcher better by sending anonymous usage stats to MM's servers:\n\n" +
        "•  launcher and Windows version\n" +
        "•  when you join MM servers, and how long you play\n" +
        "•  how many times you join other servers (never which ones)\n" +
        "•  how long mod downloads take, and any errors or crashes\n\n" +
        "No names, Steam IDs or IP addresses are kept. Your launcher is told apart only by a random number, " +
        "which is forgotten if you turn this off. You can change your answer any time in Settings.";

    /// <summary>Asks once, the first time the launcher starts with this option unanswered.</summary>
    private async Task AskAboutUsageStatsAsync()
    {
        if (_service.Settings.UsageStats != "ask" || AskYesNo == null) return;
        bool yes = await AskYesNo("Send anonymous usage stats?", UsageQuestion, "Yes, send stats", "No thanks");
        var s = Core.Config.LauncherSettings.Load();
        s.SetUsageStats(yes);
        s.Save();
        _service.ReloadSettings();
        Log.Info("Usage stats: " + (yes ? "turned on" : "turned off") + " by the player.");
    }

    /// <summary>Records one event (if stats are on) and tries to send what's waiting.</summary>
    private void TrackUsage(UsageEvent e)
    {
        try
        {
            if (!_service.Settings.SendsUsageStats)
            {
                if (_usageQueue != null || File.Exists(UsageQueue.FilePath)) { (_usageQueue ??= UsageQueue.Load()).Clear(); _usageQueue = null; }
                return;
            }
            e.At = e.At == default ? DateTimeOffset.Now : e.At;
            (_usageQueue ??= UsageQueue.Load()).Add(e);
            _ = SendUsageAsync();
        }
        catch (Exception ex) { Log.Info("Usage stats: " + ex.Message); }
    }

    private async Task SendUsageAsync()
    {
        if (_usageQueue == null || Interlocked.Exchange(ref _usageSending, 1) == 1) return;
        try
        {
            var settings = _service.Settings;
            if (!settings.SendsUsageStats) return;
            var events = _usageQueue.Snapshot();
            if (events.Count == 0) return;
            var batch = new UsageBatch
            {
                Id = settings.UsageId,
                Version = SelfUpdater.CurrentVersion.ToString(3),
                Os = OsText(),
                Events = events,
            };
            foreach (var (host, port) in _service.LastAgents.UsageAgents)
                if (await _service.Agents.SendUsageAsync(host, port, batch))
                {
                    _usageQueue.Remove(events);
                    return;
                }
        }
        finally { Interlocked.Exchange(ref _usageSending, 0); }
    }

    private static string OsText()
    {
        if (!OperatingSystem.IsWindows()) return Environment.OSVersion.Platform.ToString();
        var v = Environment.OSVersion.Version;
        return $"Windows {(v.Build >= 22000 ? "11" : "10")} ({v.Build})";
    }

    private bool IsMmServer(ServerViewModel s) => Servers.Any(m => m.Key == s.Key);
}
