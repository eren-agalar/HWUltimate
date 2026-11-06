using System.Text.RegularExpressions;

namespace NovaHardware.Tests;

/// <summary>Source-level guards for the v0.2 navigation (the WPF shell itself cannot run on the CI host).</summary>
public class NavigationTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NovaHardware.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Navigation_has_exactly_the_six_v02_pages_with_sensors_first()
    {
        var main = File.ReadAllText(Path.Combine(Root(), "src", "NovaHardware.App", "ViewModels", "MainViewModel.cs"));
        var keys = Regex.Matches(main, "\"(Nav_\\w+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(["Nav_Alerts", "Nav_Sensors", "Nav_Hardware", "Nav_Overlay", "Nav_Reports", "Nav_Settings"], keys);
        Assert.Contains("_selectedPage = Pages[0];", main, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"Pages =\s*\[\s*new\(""Nav_Sensors"""), main);
    }

    [Fact]
    public void Removed_pages_have_no_views_view_models_or_routes()
    {
        var app = Path.Combine(Root(), "src", "NovaHardware.App");
        var files = Directory.GetFiles(app, "*.*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(files, f => Regex.IsMatch(Path.GetFileName(f), "^(Dashboard|History|Stability)"));
        foreach (var f in files.Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotMatch(new Regex("DashboardViewModel|HistoryViewModel|StabilityViewModel|Nav_Dashboard|Nav_History|Nav_Stability|IStressTestService|HistoryRecorder"), text);
        }

        var csproj = File.ReadAllText(Path.Combine(app, "NovaHardware.App.csproj"));
        Assert.DoesNotContain("NovaHardware.Benchmark", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("NovaHardware.Storage", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveCharts", csproj, StringComparison.Ordinal);
    }
}
