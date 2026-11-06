using System.Text.RegularExpressions;
using NovaHardware.Core.Appearance;

namespace NovaHardware.Tests;

public class ThemeTests
{
    [Fact]
    public void All_four_themes_meet_contrast_minimums()
    {
        foreach (var p in ThemeCatalog.All) Assert.Empty(ThemeCatalog.ContrastIssues(p));
        Assert.Equal(["Nova Dark", "Nova Light", "Nova Midnight", "Nova Ocean"], ThemeCatalog.All.Select(p => p.Name));
    }

    [Fact]
    public void Dark_themes_are_dark_and_light_theme_is_light()
    {
        foreach (var p in ThemeCatalog.All)
        {
            Assert.Equal(p.IsLight, p.Background.RelativeLuminance > 0.5);
            Assert.Equal(p.IsLight, p.Text.RelativeLuminance < p.Background.RelativeLuminance);
        }
    }

    [Fact]
    public void Nova_dark_matches_the_specified_tokens()
    {
        var d = ThemeCatalog.NovaDark;
        Assert.Equal(("#11151D", "#191F2A", "#202838", "#273142", "#6595FF"), (d.Background.Hex, d.Panel.Hex, d.Surface.Hex, d.Selection.Hex, d.Accent.Hex));
        Assert.Equal(("#F3F5F8", "#929DAE", "#2A3443"), (d.Text.Hex, d.TextMuted.Hex, d.Line.Hex));
        Assert.Equal(("#36C991", "#E9AC4B", "#E45D65"), (d.Good.Hex, d.Warn.Hex, d.Bad.Hex));
    }

    [Theory]
    [InlineData(ThemeChoice.System, true, "Nova Light")]
    [InlineData(ThemeChoice.System, false, "Nova Dark")]
    [InlineData(ThemeChoice.NovaMidnight, true, "Nova Midnight")]
    [InlineData(ThemeChoice.NovaOcean, false, "Nova Ocean")]
    public void System_theme_follows_windows(ThemeChoice choice, bool systemLight, string expected) =>
        Assert.Equal(expected, ThemeCatalog.Base(choice, systemLight).Name);

    [Fact]
    public void Accent_and_status_overrides_apply_and_bad_values_are_ignored()
    {
        var p = ThemeCatalog.Resolve(new AppearanceOptions { AccentColor = "#2DD4BF", CriticalColor = "#FF0000", WarningColor = "nope" }, false);
        Assert.Equal("#2DD4BF", p.Accent.Hex);
        Assert.Equal("#2DD4BF", p.Info.Hex);
        Assert.Equal("#FF0000", p.Bad.Hex);
        Assert.Equal(ThemeCatalog.NovaDark.Warn, p.Warn);
    }

    [Fact]
    public void Accent_presets_are_readable_in_their_themes()
    {
        foreach (var (name, dark, light) in ThemeCatalog.AccentPresets)
        {
            Assert.DoesNotContain(ThemeCatalog.ContrastIssues(ThemeCatalog.Resolve(new AppearanceOptions { AccentColor = dark }, false)), i => i.Contains("accent", StringComparison.Ordinal));
            Assert.DoesNotContain(ThemeCatalog.ContrastIssues(ThemeCatalog.Resolve(new AppearanceOptions { Theme = ThemeChoice.NovaLight, AccentColor = light }, true)), i => i.Contains("accent", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Colour_math()
    {
        Assert.Equal(21, Math.Round(Rgb.Contrast(Rgb.Parse("#000000"), Rgb.Parse("#FFFFFF")), 2));
        Assert.Equal("#808080", Rgb.Parse("#000000").Mix(Rgb.Parse("#FFFFFF"), 0.5).Hex.Replace("#7F7F7F", "#808080", StringComparison.Ordinal));
        Assert.False(Rgb.TryParse("#12345", out _));
        Assert.True(Rgb.TryParse(" 6595ff ", out var c) && c.Hex == "#6595FF");
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NovaHardware.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void Design_system_styles_every_standard_control_and_uses_only_theme_tokens()
    {
        var xaml = File.ReadAllText(RepoFile("src", "NovaHardware.UI", "Themes", "Controls.xaml"));
        foreach (var control in new[] { "Button", "CheckBox", "RadioButton", "TextBox", "ComboBox", "ComboBoxItem", "Slider", "ScrollBar", "ContextMenu", "MenuItem", "ToolTip", "DataGrid", "ListBox", "ProgressBar", "ui:NovaSearchBox", "ui:NovaIconButton", "ui:NovaStatusIndicator" })
        {
            Assert.Matches(new Regex($"<Style (x:Key=\"[^\"]+\" )?TargetType=\"{Regex.Escape(control)}\"( BasedOn=\"[^\"]+\")?\\s*/?>"), xaml);
        }

        // No hard-coded colours: every brush must come from a theme token so all four themes apply everywhere.
        Assert.DoesNotMatch(new Regex("(Background|Foreground|BorderBrush|Fill|Stroke)=\"#[0-9A-Fa-f]{3,8}\""), xaml);
        Assert.DoesNotMatch(new Regex("(Background|Foreground|BorderBrush|Fill|Stroke)=\"(White|Black|Gray|LightGray|Blue)\""), xaml);
    }

    [Fact]
    public void App_views_use_dynamic_theme_brushes_only()
    {
        var views = Directory.GetFiles(RepoFile("src", "NovaHardware.App", "Views"), "*.xaml");
        Assert.NotEmpty(views);
        foreach (var file in views)
        {
            var xaml = File.ReadAllText(file);
            Assert.DoesNotMatch(new Regex("(Background|Foreground|BorderBrush|Fill|Stroke)=\"#[0-9A-Fa-f]{3,8}\""), xaml);
            Assert.DoesNotMatch(new Regex("\\{StaticResource Brush\\."), xaml);
        }
    }
}
