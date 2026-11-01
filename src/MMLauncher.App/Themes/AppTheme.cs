using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace MMLauncher.App.Themes;

/// <summary>
/// Color schemes players can pick in Settings. Every color in the launcher comes from these
/// names (the windows use them as DynamicResource), so switching re-colors everything at once.
/// </summary>
public sealed record AppTheme(string Key, string Name, bool Light, IReadOnlyDictionary<string, string> Colors)
{
    public const string DefaultKey = "mm-orange";

    /// <summary>The colors every theme has to define.</summary>
    public static readonly string[] Names =
    [
        "Orange",            // accent: links, active tabs, primary buttons, highlights (kept as "Orange" for every theme)
        "AccentHover",       // primary button under the mouse
        "AccentDisabled",    // primary button background when it can't be clicked
        "AccentDisabledText",
        "OnAccent",          // text on top of the accent color
        "AccentTint",        // soft accent background (selected server, MM notes)
        "Bg", "Chrome", "Panel", "Panel2", "Hover", "Stripe", "Line",
        "Text", "Silver", "Muted",
        "Good", "GoodTint", "GoodTintSoft",
        "Warn", "WarnTint", "WarnTintSoft",
        "Bad", "BadTint", "BadTintSoft",
    ];

    private static Dictionary<string, string> Dark(string accent, string hover, string disabled, string disabledText, string tint, string onAccent = "#0D0D0F") => new()
    {
        ["Orange"] = accent, ["AccentHover"] = hover, ["AccentDisabled"] = disabled, ["AccentDisabledText"] = disabledText,
        ["OnAccent"] = onAccent, ["AccentTint"] = tint,
        ["Bg"] = "#0D0D0F", ["Chrome"] = "#09090B", ["Panel"] = "#151518", ["Panel2"] = "#1C1C21", ["Hover"] = "#24242A",
        ["Stripe"] = "#141417", ["Line"] = "#2A2A31",
        ["Text"] = "#ECECEE", ["Silver"] = "#C0C4CC", ["Muted"] = "#8C9099",
        ["Good"] = "#3FB950", ["GoodTint"] = "#16351F", ["GoodTintSoft"] = "#132A19",
        ["Warn"] = "#E3A21A", ["WarnTint"] = "#3A2C0E", ["WarnTintSoft"] = "#2E2410",
        ["Bad"] = "#F05A4F", ["BadTint"] = "#3D1916", ["BadTintSoft"] = "#2B1513",
    };

    public static readonly IReadOnlyList<AppTheme> All =
    [
        new("mm-orange", "MM Orange (default)", false, Dark("#FF7A00", "#FF9533", "#4D2C10", "#A07A5A", "#2A1D12")),
        new("arctic-blue", "Arctic Blue", false, Dark("#3D9BFF", "#68B2FF", "#163150", "#6F8BA8", "#13202F")),
        new("toxic-green", "Toxic Green", false, Dark("#8BD33F", "#A3E063", "#2B4119", "#7F9A69", "#1A2410")),
        new("blood-red", "Blood Red", false, Dark("#E8484B", "#EE6E70", "#4A1A1B", "#A06A6B", "#2C1415")),
        new("royal-purple", "Royal Purple", false, Dark("#A970FF", "#BE93FF", "#33244D", "#8C7AA8", "#1F1630")),
        new("steel", "Steel", false, Dark("#AEB6C2", "#C9CFD8", "#33373D", "#7D838C", "#1E2126")),
        new("daylight", "Daylight (light)", true, new Dictionary<string, string>
        {
            ["Orange"] = "#D96400", ["AccentHover"] = "#F07000", ["AccentDisabled"] = "#F3D6BC", ["AccentDisabledText"] = "#9C7B5F",
            ["OnAccent"] = "#FFFFFF", ["AccentTint"] = "#FDEBDB",
            ["Bg"] = "#F3F3F6", ["Chrome"] = "#FFFFFF", ["Panel"] = "#FFFFFF", ["Panel2"] = "#F7F7FA", ["Hover"] = "#ECECF1",
            ["Stripe"] = "#F7F7FA", ["Line"] = "#DADBE1",
            ["Text"] = "#16161A", ["Silver"] = "#3C3F47", ["Muted"] = "#6A6E78",
            ["Good"] = "#1D7F36", ["GoodTint"] = "#DDF2E3", ["GoodTintSoft"] = "#EAF7EE",
            ["Warn"] = "#9A6300", ["WarnTint"] = "#FCEFD2", ["WarnTintSoft"] = "#FFF6E3",
            ["Bad"] = "#BF3A31", ["BadTint"] = "#FADFDC", ["BadTintSoft"] = "#FDEDEB",
        }),
    ];

    public static AppTheme Find(string? key) => All.FirstOrDefault(t => t.Key == key) ?? All[0];

    public Color ColorOf(string name) => Color.Parse(Colors[name]);

    /// <summary>Switches the whole launcher (every open window) to this theme.</summary>
    public void Apply()
    {
        if (Application.Current is not { } app) return;
        var res = app.Resources;
        foreach (var name in Names)
            res[name] = new SolidColorBrush(ColorOf(name));
        var accent = ColorOf("Orange");
        res["OrangeSoft"] = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        // Fluent's own controls (check boxes, text boxes, sliders) follow the system accent color.
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Mix(accent, Avalonia.Media.Colors.White, 0.15);
        res["SystemAccentColorLight2"] = Mix(accent, Avalonia.Media.Colors.White, 0.30);
        res["SystemAccentColorLight3"] = Mix(accent, Avalonia.Media.Colors.White, 0.45);
        res["SystemAccentColorDark1"] = Mix(accent, Avalonia.Media.Colors.Black, 0.15);
        res["SystemAccentColorDark2"] = Mix(accent, Avalonia.Media.Colors.Black, 0.30);
        res["SystemAccentColorDark3"] = Mix(accent, Avalonia.Media.Colors.Black, 0.45);
        app.RequestedThemeVariant = Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
