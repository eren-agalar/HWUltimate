namespace NovaHardware.Infrastructure;

/// <summary>Per-user storage locations. Everything Nova writes lives under %LOCALAPPDATA%\NovaHardware.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaHardware");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ReportDirectory);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string HistoryDatabase => Path.Combine(Root, "history.db");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string ReportDirectory => Path.Combine(Root, "reports");
}
