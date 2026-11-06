using System.Diagnostics;
using System.Windows;

namespace NovaHardware.App.Services;

public static class Ui
{
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public static void OpenFolder(string path) => Shell(path);

    public static void Shell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Serilog.Log.Warning(ex, "Could not open {Path}", path);
        }
    }

    /// <summary>Relaunches the app elevated (UAC prompt) and closes this instance.</summary>
    public static void RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--allow-multiple") { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the UAC prompt — keep running.
        }
    }
}
