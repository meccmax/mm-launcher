using System.Globalization;

namespace MMLauncher.Core.Agent;

/// <summary>
/// Works out when a server restarts next, from either
///  - fixed clock times ("00:00", "04:00", ... in the agent machine's time zone), or
///  - an interval counted from when the server process started (e.g. every 4 hours,
///    which is how CFTools / BattlEye scheduled restarts work).
/// </summary>
public static class RestartSchedule
{
    /// <summary>A restart this long overdue is still shown as "due" rather than skipped.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The next restart, or null when nothing is configured. Fixed times win over an interval.
    /// Approximate = true for interval-based guesses (a manual restart resets the count).
    /// </summary>
    public static (DateTimeOffset At, bool Approximate)? Next(
        DateTimeOffset now, DateTimeOffset? started, double everyHours, IReadOnlyList<string>? clockTimes, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var times = ParseTimes(clockTimes);
        if (times.Count > 0)
        {
            var local = TimeZoneInfo.ConvertTime(now, zone);
            for (int day = 0; day <= 1; day++)
                foreach (var t in times)
                {
                    var candidate = new DateTimeOffset(local.Date.AddDays(day) + t, zone.GetUtcOffset(local.Date.AddDays(day) + t));
                    if (candidate >= now) return (candidate, false);
                }
            return null;
        }

        if (everyHours > 0 && started is { } s && s <= now)
        {
            var interval = TimeSpan.FromHours(everyHours);
            var next = s + interval;
            // A server that ran past several intervals (restarts paused) gets its next slot,
            // but one just past its time stays "due" (it's probably restarting right now).
            while (next < now - Grace) next += interval;
            return (next, true);
        }
        return null;
    }

    /// <summary>"HH:mm" strings to times of day, sorted; invalid entries are skipped.</summary>
    public static List<TimeSpan> ParseTimes(IReadOnlyList<string>? clockTimes) =>
        (clockTimes ?? [])
            .Select(t => TimeSpan.TryParseExact(t.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var ts) && ts < TimeSpan.FromDays(1) ? ts : (TimeSpan?)null)
            .Where(t => t != null).Select(t => t!.Value).Distinct().OrderBy(t => t).ToList();

    /// <summary>"in ~1h 52m", "in 8 min", "due now".</summary>
    public static string Describe(DateTimeOffset at, bool approximate, DateTimeOffset now)
    {
        var left = at - now;
        if (left <= TimeSpan.FromSeconds(30)) return "due now";
        string tilde = approximate ? "~" : "";
        return left.TotalMinutes < 60
            ? $"in {tilde}{Math.Ceiling(left.TotalMinutes):0} min"
            : $"in {tilde}{(int)left.TotalHours}h {left.Minutes:00}m";
    }
}
