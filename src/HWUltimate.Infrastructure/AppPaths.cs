namespace HWUltimate.Infrastructure;

/// <summary>Per-user storage locations. Everything HWUltimate writes lives under %LOCALAPPDATA%\HWUltimate.</summary>
public sealed class AppPaths
{
    /// <summary>Folder used before the product was renamed to HWUltimate.</summary>
    public const string LegacyFolderName = "NovaHardware";
    public const string FolderName = "HWUltimate";

    public AppPaths(string? root = null)
    {
        if (root is null)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(local, FolderName);
            MigratedFromLegacy = MigrateLegacyData(Path.Combine(local, LegacyFolderName), root);
        }

        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ReportDirectory);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string HistoryDatabase => Path.Combine(Root, "history.db");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string ReportDirectory => Path.Combine(Root, "reports");

    /// <summary>True when this start copied the user's data from the pre-rename folder.</summary>
    public bool MigratedFromLegacy { get; }

    /// <summary>
    /// First start after the rename: copies settings, sensor preferences, reports and history from the old folder
    /// into the new one. Logs are not copied. The old folder is left untouched so nothing is lost.
    /// Runs only when the new folder does not exist yet; returns whether anything was copied.
    /// </summary>
    public static bool MigrateLegacyData(string legacyRoot, string newRoot)
    {
        if (Directory.Exists(newRoot) || !Directory.Exists(legacyRoot)) return false;
        var staging = newRoot + ".migrating";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            CopyTree(legacyRoot, staging, skipTopLevel: "logs");
            Directory.Move(staging, newRoot);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never block start-up: the app starts with defaults and the old folder still holds the data.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private static void CopyTree(string source, string target, string? skipTopLevel)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (skipTopLevel is not null && string.Equals(name, skipTopLevel, StringComparison.OrdinalIgnoreCase)) continue;
            CopyTree(dir, Path.Combine(target, name), null);
        }
    }
}
