using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NovaHardware.Infrastructure;

/// <summary>"Launch with Windows" via the per-user Run key (no admin rights, no scheduled task).</summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NovaHardware";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled, string executablePath, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\"{(startMinimized ? " --minimized" : "")}");
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }
}
