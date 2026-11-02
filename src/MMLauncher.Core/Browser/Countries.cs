using System.Globalization;

namespace MMLauncher.Core.Browser;

/// <summary>
/// Country names and continents for the browser's "Country" filter. The country of each
/// server comes from its IP address (the agent looks it up, see <see cref="GeoIpDatabase"/>),
/// so it's where the server is hosted, which usually but not always matches who plays there.
/// </summary>
public static class Countries
{
    /// <summary>Continent filter values start with this, so they can't clash with country codes.</summary>
    public const string ContinentPrefix = "@";

    public static readonly IReadOnlyList<(string Code, string Name)> Continents =
    [
        ("EU", "Europe"), ("NA", "North America"), ("SA", "South America"),
        ("AS", "Asia"), ("OC", "Oceania"), ("AF", "Africa"),
    ];

    // ISO 3166 country codes by continent. Turkey, Cyprus, Russia and the Caucasus are grouped where
    // DayZ players would look for them (Turkey and Russia under Europe, the Caucasus under Asia).
    private const string Europe = "AD AL AT AX BA BE BG BY CH CY CZ DE DK EE ES FI FO FR GB GG GI GR HR HU IE IM IS IT JE LI LT LU LV MC MD ME MK MT NL NO PL PT RO RS RU SE SI SJ SK SM TR UA VA XK";
    private const string NorthAmerica = "US CA MX GL BM PM GT BZ SV HN NI CR PA CU JM HT DO PR BS BB TT AG DM GD KN LC VC AW CW SX BQ KY TC VG VI AI MS MF BL GP MQ UM";
    private const string SouthAmerica = "AR BO BR CL CO EC FK GF GY PE PY SR UY VE GS";
    private const string Asia = "AE AF AM AZ BD BH BN BT CN GE HK ID IL IN IQ IR JO JP KG KH KP KR KW KZ LA LB LK MM MN MO MV MY NP OM PH PK PS QA SA SG SY TH TJ TL TM TW UZ VN YE IO CC CX";
    private const string Oceania = "AS AU CK FJ FM GU KI MH MP NC NF NR NU NZ PF PG PN PW SB TK TO TV VU WF WS";
    private const string Africa = "AO BF BI BJ BW CD CF CG CI CM CV DJ DZ EG EH ER ET GA GH GM GN GQ GW KE KM LR LS LY MA MG ML MR MU MW MZ NA NE NG RE RW SC SD SH SL SN SO SS ST SZ TD TG TN TZ UG YT ZA ZM ZW";

    private static readonly Dictionary<string, string> ContinentOf = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (continent, list) in new[] { ("EU", Europe), ("NA", NorthAmerica), ("SA", SouthAmerica), ("AS", Asia), ("OC", Oceania), ("AF", Africa) })
            foreach (var c in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[c] = continent;
        return map;
    }

    /// <summary>"EU", "NA", … for a country code; "" if unknown.</summary>
    public static string Continent(string? country) =>
        country is { Length: 2 } && ContinentOf.TryGetValue(country, out var c) ? c : "";

    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>English name of a country code ("DE" → "Germany"); the code itself if Windows doesn't know it.</summary>
    public static string Name(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        lock (NameCache)
        {
            if (NameCache.TryGetValue(code, out var cached)) return cached;
            string name = code.ToUpperInvariant();
            try { name = new RegionInfo(code).EnglishName; }
            catch (ArgumentException) { }
            if (code.Equals("XK", StringComparison.OrdinalIgnoreCase)) name = "Kosovo";
            NameCache[code] = name;
            return name;
        }
    }

    /// <summary>Does a server in <paramref name="country"/> match the filter value (a country code, or "@EU"-style continent)?</summary>
    public static bool Matches(string? filter, string? country)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        if (string.IsNullOrEmpty(country)) return false;
        return filter.StartsWith(ContinentPrefix, StringComparison.Ordinal)
            ? Continent(country).Equals(filter[ContinentPrefix.Length..], StringComparison.OrdinalIgnoreCase)
            : country.Equals(filter, StringComparison.OrdinalIgnoreCase);
    }
}
