using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Overlay;

namespace NovaHardware.Infrastructure.Overlay;

/// <summary>
/// Frame timing from Intel / GameTechDev PresentMon (MIT licence), run as a separate console process that reads
/// Windows ETW present events. Nothing is injected into games and anti-cheat is not touched.
/// PresentMon is not bundled with Nova: the user installs it (or places PresentMon*.exe in Nova's tools\PresentMon folder)
/// and optionally sets the path in Overlay settings. ETW tracing requires administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class PresentMonFrameSource : IFrameSource
{
    private const string SessionName = "NovaHardwareOverlay";
    private readonly ILogger<PresentMonFrameSource> _logger;
    private readonly Func<string?> _configuredPath;
    private readonly Lock _gate = new();
    private Process? _process;
    private nint _job;

    public PresentMonFrameSource(ILogger<PresentMonFrameSource> logger, Func<string?> configuredPath)
    {
        _logger = logger;
        _configuredPath = configuredPath;
    }

    public FrameSourceState State { get; private set; } = FrameSourceState.Stopped;
    public string StatusMessage { get; private set; } = "Frame timing is off.";
    public event Action<FrameSample>? FrameReceived;

    /// <summary>Configured path, then tools\PresentMon next to Nova, then the PresentMon install folder.</summary>
    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var candidates = new List<string>();
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon");
        if (Directory.Exists(local)) candidates.AddRange(Directory.GetFiles(local, "PresentMon*.exe"));
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var dir = Path.Combine(root, "Intel", "PresentMon");
            if (Directory.Exists(dir)) candidates.AddRange(Directory.GetFiles(dir, "PresentMon*.exe", SearchOption.AllDirectories));
        }

        return candidates.Where(c => !c.Contains("Service", StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c).FirstOrDefault();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            var exe = Locate(_configuredPath());
            if (exe is null)
            {
                State = FrameSourceState.NotInstalled;
                StatusMessage = "PresentMon not found. Install PresentMon or set its path in Overlay settings to show FPS.";
                return;
            }

            if (!Hardware.Elevation.IsAdministrator())
            {
                State = FrameSourceState.RequiresElevation;
                StatusMessage = "FPS needs administrator rights (Windows ETW tracing). Restart Nova as administrator.";
                return;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // PresentMon 2.x console options. The CSV header is parsed dynamically, so column layout changes are tolerated.
            foreach (var a in new[] { "--output_stdout", "--stop_existing_session", "--session_name", SessionName, "--no_console_stats" }) psi.ArgumentList.Add(a);

            try
            {
                var parser = new PresentMonCsvParser();
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) =>
                {
                    if (parser.Parse(e.Data, DateTime.UtcNow.Ticks) is { } frame) FrameReceived?.Invoke(frame);
                };
                p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogDebug("PresentMon: {Line}", e.Data); };
                p.Exited += (_, _) =>
                {
                    if (State == FrameSourceState.Running)
                    {
                        State = FrameSourceState.Failed;
                        StatusMessage = $"PresentMon exited (code {SafeExitCode(p)}).";
                        _logger.LogWarning("PresentMon exited unexpectedly");
                    }
                };
                p.Start();
                AssignToJob(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _process = p;
                State = FrameSourceState.Running;
                StatusMessage = $"PresentMon running ({Path.GetFileName(exe)}).";
                _logger.LogInformation("PresentMon started from {Path}", exe);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                State = FrameSourceState.Failed;
                StatusMessage = $"Could not start PresentMon: {ex.Message}";
                _logger.LogWarning(ex, "PresentMon start failed");
            }
        }
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "?"; }
    }

    public void Stop()
    {
        lock (_gate)
        {
            State = FrameSourceState.Stopped;
            StatusMessage = "Frame timing is off.";
            if (_process is null) return;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                _logger.LogDebug(ex, "PresentMon stop");
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }

    // ---- Job object: guarantees PresentMon is terminated if Nova exits or crashes.
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial nint CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimit info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private void AssignToJob(Process p)
    {
        if (_job == 0)
        {
            _job = CreateJobObject(0, 0);
            if (_job == 0) return;
            var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = JobObjectLimitKillOnJobClose } };
            SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimit>());
        }

        if (!AssignProcessToJobObject(_job, p.Handle)) _logger.LogDebug("AssignProcessToJobObject failed ({Error})", Marshal.GetLastPInvokeError());
    }

    public void Dispose()
    {
        Stop();
        if (_job != 0)
        {
            CloseHandle(_job);
            _job = 0;
        }
    }
}

/// <summary>Foreground window owner process, used to report FPS for the game the user is actually looking at.</summary>
[SupportedOSPlatform("windows")]
public static partial class ForegroundProcess
{
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    public static int? CurrentId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }
}
