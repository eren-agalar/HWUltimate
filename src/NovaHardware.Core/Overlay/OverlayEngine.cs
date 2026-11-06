using NovaHardware.Core.Abstractions;

namespace NovaHardware.Core.Overlay;

/// <summary>One overlay line with its recent history for the optional graph.</summary>
public sealed class OverlayLine
{
    public const int HistoryLength = 60;
    private readonly double?[] _ring = new double?[HistoryLength];
    private int _next;
    private int _count;

    public OverlayLine(OverlayMetric metric) => Metric = metric;

    public OverlayMetric Metric { get; }
    public OverlayValue Value { get; internal set; } = new(OverlayMetric.Fps, "", "N/A", null, false);

    internal void Push(double? v)
    {
        _ring[_next] = v;
        _next = (_next + 1) % HistoryLength;
        if (_count < HistoryLength) _count++;
    }

    /// <summary>History oldest → newest (copied; small fixed size).</summary>
    public double?[] History()
    {
        var result = new double?[_count];
        var start = (_next - _count + HistoryLength) % HistoryLength;
        for (var i = 0; i < _count; i++) result[i] = _ring[(start + i) % HistoryLength];
        return result;
    }
}

/// <summary>
/// Platform-neutral overlay logic. Reads values from the shared <see cref="ISensorPollingService.Current"/> snapshot
/// (it never polls hardware itself), runs the frame-timing source only while the overlay is visible and FPS metrics are
/// selected, and keeps a short history per metric for graphs. The WPF overlay window renders <see cref="Lines"/>.
/// </summary>
public sealed class OverlayEngine : IDisposable
{
    private readonly ISensorPollingService _polling;
    private readonly IFrameSource _frames;
    private readonly Func<int?> _foregroundProcess;
    private readonly FrameRateTracker _tracker;
    private readonly Lock _gate = new();
    private bool _frameSubscribed;
    private bool _disposed;

    public OverlayEngine(ISensorPollingService polling, IFrameSource frames, Func<int?> foregroundProcess, FrameRateTracker? tracker = null)
    {
        _polling = polling;
        _frames = frames;
        _foregroundProcess = foregroundProcess;
        _tracker = tracker ?? new FrameRateTracker();
    }

    public OverlaySettings Settings { get; private set; } = new();
    public bool IsVisible { get; private set; }
    public IReadOnlyList<OverlayLine> Lines { get; private set; } = [];
    public FpsReading LastFps { get; private set; } = new(FpsStatus.SourceUnavailable, null, null, null, "Frame timing is off.");
    public bool FrameSourceActive => _frameSubscribed;

    /// <summary>Raised when the engine changes settings itself (e.g. new position) so the host can persist them.</summary>
    public event EventHandler<OverlaySettings>? SettingsChanged;
    public event EventHandler<bool>? VisibilityChanged;

    public void Apply(OverlaySettings settings)
    {
        lock (_gate)
        {
            Settings = settings;
            var existing = Lines.ToDictionary(l => l.Metric);
            Lines = settings.Metrics.Distinct().Select(m => existing.GetValueOrDefault(m) ?? new OverlayLine(m)).ToList();
            UpdateFrameSource();
        }
    }

    public void Show()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsVisible) return;
            IsVisible = true;
            UpdateFrameSource();
        }

        VisibilityChanged?.Invoke(this, true);
    }

    public void Hide()
    {
        lock (_gate)
        {
            if (!IsVisible) return;
            IsVisible = false;
            UpdateFrameSource();
        }

        VisibilityChanged?.Invoke(this, false);
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    /// <summary>Starts / stops the frame source so it only runs when the overlay is visible and needs FPS.</summary>
    private void UpdateFrameSource()
    {
        var needed = IsVisible && !_disposed && OverlayPresets.NeedsFrameTelemetry(Settings.Metrics);
        if (needed && !_frameSubscribed)
        {
            _frames.FrameReceived += OnFrame;
            _frameSubscribed = true;
            _frames.Start();
        }
        else if (!needed && _frameSubscribed)
        {
            _frames.FrameReceived -= OnFrame;
            _frameSubscribed = false;
            _frames.Stop();
            _tracker.Clear();
        }
    }

    private void OnFrame(FrameSample sample) => _tracker.Add(sample);

    /// <summary>
    /// Recomputes every line from the latest shared snapshot. Call at the overlay refresh rate.
    /// <paramref name="preview"/> computes values while hidden (settings page preview); the frame source is not started for it.
    /// </summary>
    public IReadOnlyList<OverlayLine> Tick(IFormatProvider? culture = null, bool preview = false)
    {
        lock (_gate)
        {
            if (!IsVisible && !preview) return Lines;
            var snapshot = _polling.Current;
            FpsReading? fps = null;
            foreach (var line in Lines)
            {
                OverlayValue value;
                if (line.Metric is OverlayMetric.Fps or OverlayMetric.FrameTime)
                {
                    fps ??= _tracker.Read(_foregroundProcess(), _frames.State, _frames.StatusMessage);
                    var available = fps.Status is FpsStatus.Available or FpsStatus.DesktopOnly;
                    var detail = fps.Status == FpsStatus.DesktopOnly ? "desktop" : fps.Application;
                    value = line.Metric == OverlayMetric.Fps
                        ? new OverlayValue(line.Metric, OverlayMetricResolver.Label(line.Metric), available ? fps.FpsText : "N/A", available ? fps.Fps : null, available, available ? detail : fps.Message)
                        : new OverlayValue(line.Metric, OverlayMetricResolver.Label(line.Metric), available ? fps.FrameTimeText : "N/A", available ? fps.FrameTimeMs : null, available, available ? detail : fps.Message);
                }
                else
                {
                    value = OverlayMetricResolver.Resolve(line.Metric, snapshot, Settings.GpuDeviceId, culture);
                }

                line.Value = value;
                line.Push(value.Value);
            }

            if (fps is not null) LastFps = fps;
            return Lines;
        }
    }

    /// <summary>True when auto-hide rules say the overlay should currently be hidden (window stays logically visible).</summary>
    public bool ShouldAutoHide(bool novaWindowActive) =>
        (Settings.AutoHideWhenNovaActive && novaWindowActive)
        || (Settings.AutoHideWithoutGame && OverlayPresets.NeedsFrameTelemetry(Settings.Metrics) && LastFps.Status != FpsStatus.Available);

    /// <summary>Records a new window position and notifies the host to persist it.</summary>
    public void UpdatePosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top)) return;
        if (Settings.Left is { } l && Settings.Top is { } t && Math.Abs(l - left) < 0.5 && Math.Abs(t - top) < 0.5) return;
        Settings = Settings with { Left = Math.Round(left), Top = Math.Round(top) };
        SettingsChanged?.Invoke(this, Settings);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            IsVisible = false;
            UpdateFrameSource();
            _disposed = true;
        }

        _frames.Dispose();
    }
}
