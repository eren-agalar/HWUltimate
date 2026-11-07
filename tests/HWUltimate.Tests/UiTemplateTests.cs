using System.Text.RegularExpressions;

namespace HWUltimate.Tests;

/// <summary>Source-level guards for WPF templates (the WPF UI itself cannot run on the CI host).</summary>
public class UiTemplateTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HWUltimate.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void ComboBox_selection_box_honours_DisplayMemberPath()
    {
        // DisplayMemberPath is applied through ItemTemplateSelector; without it the closed box shows the record's ToString().
        var xaml = File.ReadAllText(Path.Combine(Root(), "src", "HWUltimate.UI", "Themes", "Controls.xaml"));
        Assert.Matches(new Regex(@"Content=""\{TemplateBinding SelectionBoxItem\}""[^/]*ContentTemplateSelector=""\{TemplateBinding ItemTemplateSelector\}""", RegexOptions.Singleline), xaml);
    }

    [Fact]
    public void Option_records_shown_in_dropdowns_print_their_label()
    {
        var vm = File.ReadAllText(Path.Combine(Root(), "src", "HWUltimate.App", "ViewModels", "SensorExplorerViewModel.cs"));
        Assert.Matches(new Regex(@"record FilterOption<T>\(T\? Value, string Label\)\s*\{[^}]*ToString\(\) => Label;"), vm);
        var settings = File.ReadAllText(Path.Combine(Root(), "src", "HWUltimate.App", "ViewModels", "SettingsViewModel.cs"));
        Assert.Matches(new Regex(@"record LanguageOption\(string Culture, string Name\)\s*\{[^}]*ToString\(\) => Name;"), settings);
    }

    [Fact]
    public void Logo_is_vector_and_respects_the_animation_setting()
    {
        var logo = File.ReadAllText(Path.Combine(Root(), "src", "HWUltimate.UI", "Controls", "HwuLogo.cs"));
        Assert.Contains("ThemeManager.AnimationsEnabled", logo, StringComparison.Ordinal);
        Assert.Contains("WindowState.Minimized", logo, StringComparison.Ordinal);
        Assert.Contains("SetDesiredFrameRate", logo, StringComparison.Ordinal);
        var main = File.ReadAllText(Path.Combine(Root(), "src", "HWUltimate.App", "Views", "MainWindow.xaml"));
        Assert.Contains("<ui:HwuLogo", main, StringComparison.Ordinal);
        Assert.DoesNotContain("hwultimate.png\" Width", main, StringComparison.Ordinal);

        // The application icon carries every size Windows asks for, so small sizes are drawn, not scaled.
        var ico = File.ReadAllBytes(Path.Combine(Root(), "src", "HWUltimate.App", "Assets", "hwultimate.ico"));
        Assert.Equal(10, BitConverter.ToUInt16(ico, 4));
    }
}
