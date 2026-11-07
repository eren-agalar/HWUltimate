using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HWUltimate.Infrastructure;

/// <summary>"Launch with Windows" via the per-user Run key (no admin rights, no scheduled task).</summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HWUltimate";
    /// <summary>Run value written before the rename to HWUltimate.</summary>
    private const string LegacyValueName = "NovaHardware";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string || key?.GetValue(LegacyValueName) is string;
    }

    public static void Set(bool enabled, string executablePath, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\"{(startMinimized ? " --minimized" : "")}");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
        // The old entry points at NovaHardware.exe, which no longer exists after the rename.
        if (key.GetValue(LegacyValueName) is not null) key.DeleteValue(LegacyValueName);
    }

    /// <summary>Replaces a pre-rename Run entry with one for this executable, keeping the minimized flag.</summary>
    public static void MigrateLegacy(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(LegacyValueName) is not string old) return;
        Set(true, executablePath, old.Contains("--minimized", StringComparison.OrdinalIgnoreCase));
    }
}
