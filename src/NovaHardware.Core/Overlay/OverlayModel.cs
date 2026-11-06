using NovaHardware.Core.Sensors;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Overlay;

public enum OverlayMetric
{
    CpuTemperature = 0,
    CpuUtilization,
    CpuClock,
    GpuTemperature,
    GpuHotspot,
    GpuUtilization,
    GpuClock,
    GpuPower,
    GpuVram,
    GpuFan,
    RamUsed,
    RamUtilization,
    Fps,
    FrameTime,
}

public enum OverlayPreset { Minimal = 0, Gaming, FullHardware, Custom }

public enum OverlayOrientation { Vertical = 0, Horizontal }

public sealed record OverlaySettings
{
    public bool ShowOnStartup { get; init; }
    public OverlayPreset Preset { get; init; } = OverlayPreset.Gaming;
    public List<OverlayMetric> Metrics { get; init; } = [.. OverlayPresets.For(OverlayPreset.Gaming)];
    public double BackgroundOpacity { get; init; } = 0.72;
    public double Scale { get; init; } = 1.0;
    public double FontSize { get; init; } = 13;
    public string TextColor { get; init; } = "#F3F5F8";
    public string AccentColor { get; init; } = "#6595FF";
    public string BackgroundColor { get; init; } = "#11151D";
    public bool ShowGraphs { get; init; }
    public bool ShowLabels { get; init; } = true;
    public int RefreshIntervalMs { get; init; } = 1000;
    public double? Left { get; init; }
    public double? Top { get; init; }
    public bool ClickThrough { get; init; }
    public string Hotkey { get; init; } = "Ctrl+Shift+F10";
    public bool AutoHideWhenNovaActive { get; init; }
    public bool AutoHideWithoutGame { get; init; }
    public OverlayOrientation Orientation { get; init; } = OverlayOrientation.Vertical;
    /// <summary>Physical GPU device id to show; null picks the discrete GPU automatically.</summary>
    public string? GpuDeviceId { get; init; }
    /// <summary>Optional path to PresentMon.exe (not bundled; see the README for licensing).</summary>
    public string? PresentMonPath { get; init; }

    public static IReadOnlyList<int> RefreshIntervals { get; } = [250, 500, 1000, 2000];
}

public static class OverlayPresets
{
    public static IReadOnlyList<OverlayMetric> For(OverlayPreset preset) => preset switch
    {
        OverlayPreset.Minimal => [OverlayMetric.Fps, OverlayMetric.CpuTemperature, OverlayMetric.GpuTemperature],
        OverlayPreset.Gaming => [OverlayMetric.Fps, OverlayMetric.FrameTime, OverlayMetric.CpuTemperature, OverlayMetric.CpuUtilization,
            OverlayMetric.GpuTemperature, OverlayMetric.GpuUtilization, OverlayMetric.GpuPower, OverlayMetric.GpuVram],
        OverlayPreset.FullHardware => Enum.GetValues<OverlayMetric>(),
        _ => [],
    };

    public static bool NeedsFrameTelemetry(IEnumerable<OverlayMetric> metrics) => metrics.Any(m => m is OverlayMetric.Fps or OverlayMetric.FrameTime);
}

/// <summary>A rendered overlay line. <see cref="Text"/> is "N/A" when the hardware or telemetry source has no value.</summary>
public sealed record OverlayValue(OverlayMetric Metric, string Label, string Text, double? Value, bool IsAvailable, string? Detail = null);

/// <summary>Picks overlay values from the shared sensor snapshot (no polling of its own). Metadata is never shown.</summary>
public static class OverlayMetricResolver
{
    public static string Label(OverlayMetric m) => m switch
    {
        OverlayMetric.CpuTemperature => "CPU",
        OverlayMetric.CpuUtilization => "CPU load",
        OverlayMetric.CpuClock => "CPU clock",
        OverlayMetric.GpuTemperature => "GPU",
        OverlayMetric.GpuHotspot => "Hotspot",
        OverlayMetric.GpuUtilization => "GPU load",
        OverlayMetric.GpuClock => "GPU clock",
        OverlayMetric.GpuPower => "GPU power",
        OverlayMetric.GpuVram => "VRAM",
        OverlayMetric.GpuFan => "GPU fan",
        OverlayMetric.RamUsed => "RAM",
        OverlayMetric.RamUtilization => "RAM load",
        OverlayMetric.Fps => "FPS",
        OverlayMetric.FrameTime => "Frame time",
        _ => m.ToString(),
    };

    /// <summary>Explicit choice if present; otherwise the GPU with the most VRAM, then the one with most providers (typically discrete).</summary>
    public static HardwareDevice? PickGpu(SensorSnapshot s, string? preferredId)
    {
        var gpus = s.DevicesOf(HardwareCategory.Gpu).ToList();
        if (gpus.Count == 0) return null;
        if (preferredId is not null && gpus.FirstOrDefault(g => g.DeviceId == preferredId) is { } chosen) return chosen;
        double Vram(HardwareDevice g) => s.ForDevice(g.DeviceId)
            .Where(x => x.IsMeasurement && x.Type is SensorType.Memory or SensorType.MemorySmall && x.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) && x.Value.HasValue)
            .Select(x => x.Unit == SensorUnit.Gigabyte ? x.Value!.Value * 1024 : x.Value!.Value).DefaultIfEmpty(0).Max();
        return gpus.OrderByDescending(Vram).ThenByDescending(g => g.Providers.Count).First();
    }

    private static SensorReading? On(SensorSnapshot s, HardwareDevice? device, SensorType type, params string[] names)
    {
        if (device is null) return null;
        var candidates = s.ForDevice(device.DeviceId).Where(x => x.IsMeasurement && x.Type == type && x.Value.HasValue).ToList();
        foreach (var n in names)
        {
            var m = candidates.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                 ?? candidates.FirstOrDefault(x => x.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }

        return null;
    }

    public static OverlayValue Resolve(OverlayMetric metric, SensorSnapshot s, string? gpuDeviceId, IFormatProvider? culture = null)
    {
        culture ??= System.Globalization.CultureInfo.CurrentCulture;
        var gpu = PickGpu(s, gpuDeviceId);
        SensorReading? r = metric switch
        {
            OverlayMetric.CpuTemperature => s.CpuTemperature(),
            OverlayMetric.CpuUtilization => s.CpuLoad(),
            OverlayMetric.CpuClock => s.CpuClock(),
            OverlayMetric.GpuTemperature => On(s, gpu, SensorType.Temperature, "GPU Core", "Core", "GPU Temperature", "Edge"),
            OverlayMetric.GpuHotspot => On(s, gpu, SensorType.Temperature, "GPU Hot Spot", "Hot Spot", "Hotspot"),
            OverlayMetric.GpuUtilization => On(s, gpu, SensorType.Utilization, "GPU Core", "D3D 3D", "Core"),
            OverlayMetric.GpuClock => On(s, gpu, SensorType.Clock, "GPU Core", "Core", "Graphics"),
            OverlayMetric.GpuPower => On(s, gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power", "Total"),
            OverlayMetric.GpuFan => On(s, gpu, SensorType.FanSpeed, "GPU Fan", "Fan"),
            OverlayMetric.RamUtilization => s.MemoryLoad(),
            _ => null,
        };

        switch (metric)
        {
            case OverlayMetric.GpuVram:
            {
                var used = On(s, gpu, SensorType.MemorySmall, "GPU Memory Used", "VRAM Used", "Memory Used") ?? On(s, gpu, SensorType.Memory, "GPU Memory Used", "Memory Used");
                if (used is null) return new(metric, Label(metric), "N/A", null, false);
                var mb = used.Unit == SensorUnit.Gigabyte ? used.Value!.Value * 1024 : used.Value!.Value;
                var total = On(s, gpu, SensorType.MemorySmall, "GPU Memory Total", "VRAM Total");
                var text = mb >= 1024 ? $"{(mb / 1024).ToString("0.0", culture)} GB" : $"{mb.ToString("0", culture)} MB";
                return new(metric, Label(metric), text, mb, true, total is { Value: > 0 } t ? $"{(100 * mb / t.Value!.Value).ToString("0", culture)} %" : null);
            }

            case OverlayMetric.RamUsed:
            {
                var used = s.MemoryUsed();
                return used is null ? new(metric, Label(metric), "N/A", null, false)
                    : new(metric, Label(metric), used.Unit.FormatValue(used.Value, culture), used.Value, true, s.MemoryLoad() is { } l ? l.Unit.FormatValue(l.Value, culture) : null);
            }

            case OverlayMetric.Fps or OverlayMetric.FrameTime:
                throw new ArgumentException("Frame metrics come from FrameRateTracker, not from the sensor snapshot.", nameof(metric));
        }

        if (r is null) return new(metric, Label(metric), "N/A", null, false);
        return new(metric, Label(metric), $"{r.Value!.Value.ToString("0", culture)} {r.UnitSymbol}".Trim(), r.Value, true);
    }
}
