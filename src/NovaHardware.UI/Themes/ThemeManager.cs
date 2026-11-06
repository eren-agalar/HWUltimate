using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NovaHardware.Core.Appearance;

namespace NovaHardware.UI.Themes;

/// <summary>
/// Applies Nova design tokens (colours, density, typography) to application resources. Every style references the
/// tokens through DynamicResource, so a change repaints all windows, dialogs, menus, popups and the overlay immediately.
/// </summary>
public static class ThemeManager
{
    public static AppearanceOptions Options { get; private set; } = new();
    public static ThemePalette Palette { get; private set; } = ThemeCatalog.NovaDark;

    /// <summary>Global animation switch read by Nova controls (chevrons, fades).</summary>
    public static bool AnimationsEnabled { get; private set; } = true;

    public static event EventHandler? Changed;

    public static void Apply(AppearanceOptions options)
    {
        Options = options;
        Palette = ThemeCatalog.Resolve(options, SystemTheme.UsesLightTheme());
        AnimationsEnabled = options.AnimationsEnabled;
        var r = Application.Current.Resources;
        foreach (var (key, color) in Tokens(Palette))
        {
            var brush = new SolidColorBrush(ToColor(color));
            brush.Freeze();
            r[key] = brush;
            r[key.Replace("Brush.", "Color.", StringComparison.Ordinal)] = ToColor(color);
        }

        var compact = options.Density == InterfaceDensity.Compact;
        r["Size.Control"] = compact ? 30.0 : 34.0;
        r["Size.Row"] = compact ? 22.0 : 26.0;
        r["Size.RowAdvanced"] = compact ? 38.0 : 44.0;
        r["Thickness.Control"] = compact ? new Thickness(10, 4, 10, 4) : new Thickness(14, 6, 14, 6);
        var (font, small) = options.Typography switch { TypographyScale.Small => (12.0, 11.0), TypographyScale.Large => (14.0, 12.5), _ => (13.0, 11.5) };
        r["Size.Font"] = font;
        r["Size.FontSmall"] = small;
        r["Size.FontTitle"] = font * 2;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Re-applies the current options (used when Windows' light / dark setting changes).</summary>
    public static void Refresh() => Apply(Options);

    public static IEnumerable<(string Key, Rgb Color)> Tokens(ThemePalette p) =>
    [
        ("Brush.Background", p.Background),
        ("Brush.Sidebar", p.Sidebar),
        ("Brush.Panel", p.Panel),
        ("Brush.Surface", p.Surface),
        ("Brush.Selection", p.Selection),
        ("Brush.PanelHover", p.Hover),
        ("Brush.RowAlt", p.RowAlternate),
        ("Brush.Line", p.Line),
        ("Brush.Text", p.Text),
        ("Brush.TextMuted", p.TextMuted),
        ("Brush.Accent", p.Accent),
        ("Brush.AccentSoft", p.AccentSoft),
        ("Brush.OnAccent", p.OnAccent),
        ("Brush.Good", p.Good),
        ("Brush.Warn", p.Warn),
        ("Brush.Bad", p.Bad),
        ("Brush.Info", p.Info),
        ("Brush.Series1", p.Accent),
        ("Brush.Series2", p.Series2),
        ("Brush.Series3", p.Good),
        ("Brush.Shadow", p.IsLight ? new Rgb(90, 100, 120) : new Rgb(0, 0, 0)),
    ];

    public static Color ToColor(Rgb c) => System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);

    public static Color Color(string brushKey) =>
        Application.Current.TryFindResource(brushKey) is SolidColorBrush b ? b.Color : Colors.Gray;
}

/// <summary>Reads and watches Windows' "app mode" (light / dark) setting.</summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static bool _watching;

    public static bool UsesLightTheme()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Re-applies the theme when Windows' colour settings change and Nova follows the system theme.</summary>
    public static void StartWatching()
    {
        if (_watching || !OperatingSystem.IsWindows()) return;
        _watching = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle
                && ThemeManager.Options.Theme == ThemeChoice.System)
            {
                Application.Current?.Dispatcher.InvokeAsync(ThemeManager.Refresh);
            }
        };
    }
}
