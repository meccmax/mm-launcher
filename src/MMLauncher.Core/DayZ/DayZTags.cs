using System.Text.RegularExpressions;

namespace MMLauncher.Core.DayZ;

/// <summary>
/// DayZ packs extra server details into the A2S keywords string, e.g.
/// "battleye,no3rd,external,privHive,shard,lqs0,etm4.000000,entm8.000000,mod,14:32".
/// </summary>
public sealed partial record DayZTags
{
    public string? TimeOfDay { get; init; }
    public int? Queue { get; init; }
    public bool FirstPersonOnly { get; init; }
    public bool BattlEye { get; init; }
    public bool PrivateHive { get; init; }
    public bool External { get; init; }
    public double? DayAcceleration { get; init; }
    public double? NightAcceleration { get; init; }
    public IReadOnlyList<string> Raw { get; init; } = [];

    /// <summary>True when the in-game clock is between 06:00 and 19:59 (rough "daylight").</summary>
    public bool? IsDaytime
    {
        get
        {
            if (TimeOfDay is null || !int.TryParse(TimeOfDay.Split(':')[0], out int hour)) return null;
            return hour is >= 6 and < 20;
        }
    }

    public static DayZTags Parse(string keywords)
    {
        var tags = keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? time = null;
        int? queue = null;
        double? etm = null, entm = null;

        foreach (var t in tags)
        {
            if (TimeRegex().IsMatch(t)) time = t;
            else if (t.StartsWith("lqs") && int.TryParse(t[3..], out var q)) queue = q;
            else if (t.StartsWith("entm") && TryNum(t[4..], out var n)) entm = n;
            else if (t.StartsWith("etm") && TryNum(t[3..], out var d)) etm = d;
        }

        return new DayZTags
        {
            TimeOfDay = time,
            Queue = queue,
            FirstPersonOnly = tags.Contains("no3rd"),
            BattlEye = tags.Contains("battleye"),
            PrivateHive = tags.Contains("privHive"),
            External = tags.Contains("external"),
            DayAcceleration = etm,
            NightAcceleration = entm,
            Raw = tags,
        };
    }

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

    [GeneratedRegex(@"^\d{1,2}:\d{2}$")]
    private static partial Regex TimeRegex();
}
