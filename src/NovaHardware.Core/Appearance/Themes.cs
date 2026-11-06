using System.Globalization;

namespace NovaHardware.Core.Appearance;

public enum ThemeChoice { NovaDark = 0, NovaLight, NovaMidnight, NovaOcean, System }

public enum InterfaceDensity { Compact = 0, Comfortable }

public enum TypographyScale { Small = 0, Default, Large }

/// <summary>sRGB colour with WCAG luminance / contrast helpers.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string hex)
    {
        if (!TryParse(hex, out var c)) throw new FormatException($"'{hex}' is not a #RRGGBB colour.");
        return c;
    }

    public static bool TryParse(string? hex, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public string Hex => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    public double RelativeLuminance
    {
        get
        {
            static double C(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * C(R) + 0.7152 * C(G) + 0.0722 * C(B);
        }
    }

    public static double Contrast(Rgb a, Rgb b)
    {
        var (hi, lo) = a.RelativeLuminance >= b.RelativeLuminance ? (a.RelativeLuminance, b.RelativeLuminance) : (b.RelativeLuminance, a.RelativeLuminance);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Linear blend: t = 0 → this, t = 1 → other.</summary>
    public Rgb Mix(Rgb other, double t) => new(
        (byte)Math.Round(R + (other.R - R) * t), (byte)Math.Round(G + (other.G - G) * t), (byte)Math.Round(B + (other.B - B) * t));
}

/// <summary>All colour tokens of a theme. Every view, control and overlay draws only with these.</summary>
public sealed record ThemePalette
{
    public required string Name { get; init; }
    public required bool IsLight { get; init; }
    public required Rgb Background { get; init; }
    public required Rgb Sidebar { get; init; }
    public required Rgb Panel { get; init; }
    public required Rgb Surface { get; init; }
    public required Rgb Selection { get; init; }
    public required Rgb Line { get; init; }
    public required Rgb Text { get; init; }
    public required Rgb TextMuted { get; init; }
    public required Rgb Accent { get; init; }
    public required Rgb Good { get; init; }
    public required Rgb Warn { get; init; }
    public required Rgb Bad { get; init; }
    public required Rgb Info { get; init; }
    public required Rgb Series2 { get; init; }

    /// <summary>Very subtle alternate row background.</summary>
    public Rgb RowAlternate => Panel.Mix(Surface, IsLight ? 0.55 : 0.35);
    /// <summary>Hover background for rows and buttons.</summary>
    public Rgb Hover => Panel.Mix(Selection, 0.6);
    /// <summary>Accent at low intensity, used for selected items.</summary>
    public Rgb AccentSoft => Panel.Mix(Accent, IsLight ? 0.14 : 0.22);
    /// <summary>Text drawn on the accent colour: white or near-black, whichever contrasts more.</summary>
    public Rgb OnAccent => Rgb.Contrast(Accent, new Rgb(255, 255, 255)) >= Rgb.Contrast(Accent, new Rgb(17, 21, 29)) ? new Rgb(255, 255, 255) : new Rgb(17, 21, 29);
}

/// <summary>User customisations applied on top of a theme.</summary>
public sealed record AppearanceOptions
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.NovaDark;
    /// <summary>#RRGGBB accent override; null keeps the theme accent.</summary>
    public string? AccentColor { get; init; }
    public string? NormalColor { get; init; }
    public string? WarningColor { get; init; }
    public string? CriticalColor { get; init; }
    public bool AnimationsEnabled { get; init; } = true;
    public InterfaceDensity Density { get; init; } = InterfaceDensity.Compact;
    public TypographyScale Typography { get; init; } = TypographyScale.Default;
}

public static class ThemeCatalog
{
    private static Rgb C(string hex) => Rgb.Parse(hex);

    public static ThemePalette NovaDark { get; } = new()
    {
        Name = "Nova Dark", IsLight = false,
        Background = C("#11151D"), Sidebar = C("#0E1219"), Panel = C("#191F2A"), Surface = C("#202838"), Selection = C("#273142"),
        Line = C("#2A3443"), Text = C("#F3F5F8"), TextMuted = C("#929DAE"), Accent = C("#6595FF"),
        Good = C("#36C991"), Warn = C("#E9AC4B"), Bad = C("#E45D65"), Info = C("#6595FF"), Series2 = C("#B48CFF"),
    };

    public static ThemePalette NovaLight { get; } = new()
    {
        Name = "Nova Light", IsLight = true,
        Background = C("#F3F5F8"), Sidebar = C("#FFFFFF"), Panel = C("#FFFFFF"), Surface = C("#EEF1F5"), Selection = C("#DCE6FA"),
        Line = C("#DDE2EA"), Text = C("#141A24"), TextMuted = C("#586374"), Accent = C("#2459D6"),
        Good = C("#0F7A53"), Warn = C("#8F5B05"), Bad = C("#BC2C36"), Info = C("#2459D6"), Series2 = C("#7A3FD1"),
    };

    public static ThemePalette NovaMidnight { get; } = new()
    {
        Name = "Nova Midnight", IsLight = false,
        Background = C("#07080B"), Sidebar = C("#050608"), Panel = C("#0E1015"), Surface = C("#151820"), Selection = C("#1E2331"),
        Line = C("#1E222C"), Text = C("#ECEFF4"), TextMuted = C("#8F97A7"), Accent = C("#7C9CFF"),
        Good = C("#3DD39B"), Warn = C("#F0B455"), Bad = C("#EF6A72"), Info = C("#7C9CFF"), Series2 = C("#C39BFF"),
    };

    public static ThemePalette NovaOcean { get; } = new()
    {
        Name = "Nova Ocean", IsLight = false,
        Background = C("#0A1322"), Sidebar = C("#08101C"), Panel = C("#0F1E35"), Surface = C("#152943"), Selection = C("#1C3558"),
        Line = C("#203654"), Text = C("#EEF3FA"), TextMuted = C("#94A8C4"), Accent = C("#4FA3FF"),
        Good = C("#34C8A0"), Warn = C("#E8B04E"), Bad = C("#E9696F"), Info = C("#4FA3FF"), Series2 = C("#9D8CFF"),
    };

    public static IReadOnlyList<ThemePalette> All { get; } = [NovaDark, NovaLight, NovaMidnight, NovaOcean];

    /// <summary>The concrete palette for a choice. "System" follows Windows' app light / dark setting.</summary>
    public static ThemePalette Base(ThemeChoice choice, bool systemUsesLightTheme) => choice switch
    {
        ThemeChoice.NovaLight => NovaLight,
        ThemeChoice.NovaMidnight => NovaMidnight,
        ThemeChoice.NovaOcean => NovaOcean,
        ThemeChoice.System => systemUsesLightTheme ? NovaLight : NovaDark,
        _ => NovaDark,
    };

    /// <summary>Applies user overrides (accent and status colours); invalid colour strings are ignored.</summary>
    public static ThemePalette Resolve(AppearanceOptions options, bool systemUsesLightTheme)
    {
        var p = Base(options.Theme, systemUsesLightTheme);
        Rgb Pick(string? hex, Rgb fallback) => Rgb.TryParse(hex, out var c) ? c : fallback;
        var accent = Pick(options.AccentColor, p.Accent);
        return p with
        {
            Accent = accent,
            Info = Rgb.TryParse(options.AccentColor, out _) ? accent : p.Info,
            Good = Pick(options.NormalColor, p.Good),
            Warn = Pick(options.WarningColor, p.Warn),
            Bad = Pick(options.CriticalColor, p.Bad),
        };
    }

    /// <summary>Accent presets offered in Settings (each tested for contrast in both dark and light themes).</summary>
    public static IReadOnlyList<(string Name, string Dark, string Light)> AccentPresets { get; } =
    [
        ("Nova Blue", "#6595FF", "#2459D6"),
        ("Violet", "#A78BFA", "#6D3FD6"),
        ("Teal", "#2DD4BF", "#0A6B60"),
        ("Amber", "#F5B048", "#8F5B05"),
        ("Rose", "#F2738C", "#B42850"),
        ("Graphite", "#AEB6C4", "#4A5464"),
    ];

    /// <summary>Contrast problems for a palette (empty when every pairing meets the Nova minimums).</summary>
    public static IReadOnlyList<string> ContrastIssues(ThemePalette p)
    {
        var issues = new List<string>();
        void Check(string name, Rgb fg, double min)
        {
            foreach (var (bgName, bg) in new[] { ("panel", p.Panel), ("background", p.Background), ("selection", p.Selection) })
            {
                var c = Rgb.Contrast(fg, bg);
                if (c < min) issues.Add($"{p.Name}: {name} on {bgName} is {c:0.00}:1 (minimum {min}:1)");
            }
        }

        Check("text", p.Text, 7);
        Check("secondary text", p.TextMuted, 4.5);
        Check("accent", p.Accent, 4.5);
        Check("normal status", p.Good, 3);
        Check("warning status", p.Warn, 3);
        Check("critical status", p.Bad, 3);
        if (Rgb.Contrast(p.OnAccent, p.Accent) < 3) issues.Add($"{p.Name}: text on accent is below 3:1");
        return issues;
    }
}
