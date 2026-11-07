using System.Text.RegularExpressions;

namespace HWUltimate.Tests;

/// <summary>Source-level guards for the v0.2 navigation (the WPF shell itself cannot run on the CI host).</summary>
public class NavigationTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HWUltimate.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Navigation_has_exactly_the_four_pages_with_sensors_first()
    {
        var main = File.ReadAllText(Path.Combine(Root(), "src", "HWUltimate.App", "ViewModels", "MainViewModel.cs"));
        var keys = Regex.Matches(main, "\"(Nav_\\w+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(["Nav_Sensors", "Nav_Hardware", "Nav_Reports", "Nav_Settings"], keys);
        Assert.Contains("_selectedPage = Pages[0];", main, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"Pages =\s*\[\s*new\(""Nav_Sensors"""), main);
    }

    [Fact]
    public void Removed_pages_have_no_views_view_models_or_routes()
    {
        var app = Path.Combine(Root(), "src", "HWUltimate.App");
        var files = Directory.GetFiles(app, "*.*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(files, f => Regex.IsMatch(Path.GetFileName(f), "^(Dashboard|History|Stability|Overlay|Alerts)"));
        foreach (var f in files.Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotMatch(new Regex("DashboardViewModel|HistoryViewModel|StabilityViewModel|Nav_Dashboard|Nav_History|Nav_Stability|IStressTestService|HistoryRecorder|OverlayViewModel|AlertsViewModel|OverlayController|IAlertService|IFrameSource|Nav_Overlay|Nav_Alerts"), text);
        }

        var csproj = File.ReadAllText(Path.Combine(app, "HWUltimate.App.csproj"));
        Assert.DoesNotContain("HWUltimate.Benchmark", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("HWUltimate.Storage", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveCharts", csproj, StringComparison.Ordinal);
    }
}
