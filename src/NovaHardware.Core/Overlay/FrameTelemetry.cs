using System.Globalization;

namespace NovaHardware.Core.Overlay;

/// <summary>One presented frame as reported by a frame-timing source (e.g. PresentMon).</summary>
public readonly record struct FrameSample(int ProcessId, string Application, double FrameTimeMs, long TimestampTicks);

public enum FrameSourceState
{
    /// <summary>No supported frame source is installed / configured.</summary>
    NotInstalled = 0,
    /// <summary>The source needs administrator rights (ETW tracing).</summary>
    RequiresElevation,
    /// <summary>Configured but not running (overlay hidden or FPS metrics disabled).</summary>
    Stopped,
    Running,
    /// <summary>The source process exited or failed.</summary>
    Failed,
}

/// <summary>A frame-timing source. Implementations never inject into games; they consume OS-level present events.</summary>
public interface IFrameSource : IDisposable
{
    FrameSourceState State { get; }
    string StatusMessage { get; }
    event Action<FrameSample>? FrameReceived;
    void Start();
    void Stop();
}

public enum FpsStatus { Available = 0, SourceUnavailable, NoForegroundApplication, DesktopOnly, Waiting }

/// <summary>Frame rate of one application, or why it is not available.</summary>
public sealed record FpsReading(FpsStatus Status, double? Fps, double? FrameTimeMs, string? Application, string Message)
{
    public string FpsText => Fps is { } f ? f.ToString("0", CultureInfo.CurrentCulture) : "N/A";
    public string FrameTimeText => FrameTimeMs is { } t ? $"{t.ToString("0.0", CultureInfo.CurrentCulture)} ms" : "N/A";
}

/// <summary>
/// Aggregates frame samples per process and reports FPS / average frame time for the foreground application only,
/// over a sliding window. Desktop compositor / shell processes are reported as "desktop", not as a game frame rate.
/// </summary>
public sealed class FrameRateTracker
{
    private static readonly HashSet<string> DesktopProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "explorer.exe", "NovaHardware.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe",
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Queue<FrameSample>> _byProcess = [];
    private readonly long _windowTicks;
    private readonly Func<long> _now;

    public FrameRateTracker(TimeSpan? window = null, Func<long>? clock = null)
    {
        _windowTicks = (window ?? TimeSpan.FromSeconds(1)).Ticks;
        _now = clock ?? (() => DateTime.UtcNow.Ticks);
    }

    public static bool IsDesktopProcess(string application) => DesktopProcesses.Contains(application);

    public void Add(FrameSample sample)
    {
        if (sample.FrameTimeMs is <= 0 or > 5000 || double.IsNaN(sample.FrameTimeMs)) return;
        lock (_gate)
        {
            if (!_byProcess.TryGetValue(sample.ProcessId, out var q)) _byProcess[sample.ProcessId] = q = new Queue<FrameSample>();
            q.Enqueue(sample);
            Trim(q, _now());
        }
    }

    private void Trim(Queue<FrameSample> q, long now)
    {
        while (q.Count > 0 && now - q.Peek().TimestampTicks > _windowTicks * 2) q.Dequeue();
    }

    /// <summary>FPS for the given foreground process. Never invents a value: no frames → status explains why.</summary>
    public FpsReading Read(int? foregroundProcessId, FrameSourceState sourceState, string sourceMessage)
    {
        if (sourceState != FrameSourceState.Running) return new FpsReading(FpsStatus.SourceUnavailable, null, null, null, sourceMessage);
        if (foregroundProcessId is not { } pid) return new FpsReading(FpsStatus.NoForegroundApplication, null, null, null, "No foreground application.");
        lock (_gate)
        {
            var now = _now();
            foreach (var q in _byProcess.Values) Trim(q, now);
            if (!_byProcess.TryGetValue(pid, out var queue) || queue.Count == 0)
            {
                return new FpsReading(FpsStatus.Waiting, null, null, null, "The foreground application is not presenting frames.");
            }

            var recent = queue.Where(f => now - f.TimestampTicks <= _windowTicks).ToList();
            if (recent.Count == 0) return new FpsReading(FpsStatus.Waiting, null, null, queue.Peek().Application, "Waiting for frames.");
            var app = recent[^1].Application;
            var avg = recent.Average(f => f.FrameTimeMs);
            var fps = 1000.0 / avg;
            return IsDesktopProcess(app)
                ? new FpsReading(FpsStatus.DesktopOnly, fps, avg, app, "Desktop / shell frame rate — not a game.")
                : new FpsReading(FpsStatus.Available, fps, avg, app, app);
        }
    }

    public void Clear()
    {
        lock (_gate) _byProcess.Clear();
    }
}

/// <summary>
/// Parses PresentMon CSV output. The header row is read dynamically so both v1 ("MsBetweenPresents") and
/// v2 ("FrameTime" / "MsBetweenPresents") column layouts are supported.
/// </summary>
public sealed class PresentMonCsvParser
{
    private static readonly string[] FrameTimeColumns = ["FrameTime", "MsBetweenPresents", "msBetweenPresents", "MsBetweenDisplayChange"];
    private int _app = -1, _pid = -1, _frame = -1;

    public bool HasHeader => _frame >= 0;

    /// <summary>Feeds one line. Returns a sample for data rows, null for header / malformed / unrelated rows.</summary>
    public FrameSample? Parse(string? line, long timestampTicks)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var cells = line.Split(',');
        if (!HasHeader || cells[0].Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            if (!cells[0].Equals("Application", StringComparison.OrdinalIgnoreCase)) return null;
            _app = 0;
            _pid = Array.FindIndex(cells, c => c.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
            _frame = -1;
            foreach (var name in FrameTimeColumns)
            {
                _frame = Array.FindIndex(cells, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (_frame >= 0) break;
            }

            return null;
        }

        if (_pid < 0 || cells.Length <= Math.Max(_pid, _frame)) return null;
        if (!int.TryParse(cells[_pid], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        if (!double.TryParse(cells[_frame], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return null;
        return new FrameSample(pid, cells[_app], ms, timestampTicks);
    }
}

/// <summary>Keyboard shortcut such as "Ctrl+Shift+F10" with Win32 RegisterHotKey modifier flags and virtual key.</summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8;

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Enter a shortcut, e.g. Ctrl+Shift+F10."; return false; }
        uint mods = 0;
        uint? vk = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= Control; break;
                case "ALT": mods |= Alt; break;
                case "SHIFT": mods |= Shift; break;
                case "WIN" or "WINDOWS": mods |= Win; break;
                default:
                    if (vk is not null) { error = "Only one non-modifier key is allowed."; return false; }
                    vk = KeyCode(raw);
                    if (vk is null) { error = $"'{raw}' is not a supported key."; return false; }
                    break;
            }
        }

        if (vk is null) { error = "A shortcut needs a key besides the modifiers."; return false; }
        var isFunctionKey = vk is >= 0x70 and <= 0x87;
        if (mods == 0 && !isFunctionKey) { error = "Use at least one modifier (Ctrl, Alt, Shift or Win) unless the key is F1–F24."; return false; }
        gesture = new HotkeyGesture(mods, vk.Value);
        return true;
    }

    private static uint? KeyCode(string key)
    {
        var k = key.ToUpperInvariant();
        if (k.Length == 1 && k[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return k[0];
        if (k.Length >= 2 && k[0] == 'F' && int.TryParse(k[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24) return (uint)(0x6F + f);
        return k switch
        {
            "HOME" => 0x24, "END" => 0x23, "INSERT" or "INS" => 0x2D, "DELETE" or "DEL" => 0x2E,
            "PAGEUP" or "PGUP" => 0x21, "PAGEDOWN" or "PGDN" => 0x22, "SPACE" => 0x20, "PAUSE" => 0x13, "SCROLLLOCK" => 0x91,
            _ => null,
        };
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Win) != 0) parts.Add("Win");
        parts.Add(VirtualKey switch
        {
            >= 0x70 and <= 0x87 => $"F{VirtualKey - 0x6F}",
            0x24 => "Home", 0x23 => "End", 0x2D => "Insert", 0x2E => "Delete", 0x21 => "PageUp", 0x22 => "PageDown",
            0x20 => "Space", 0x13 => "Pause", 0x91 => "ScrollLock",
            _ => ((char)VirtualKey).ToString(),
        });
        return string.Join('+', parts);
    }
}
