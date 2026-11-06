using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Alerts;

/// <summary>A resolved threshold and where it came from.</summary>
public sealed record ThresholdLevel(double Value, AlertKind Kind, string Source);

public sealed record ResolvedThresholds(ThresholdLevel? Warning, ThresholdLevel? Critical);

/// <summary>
/// Component-specific thresholds. Order of precedence: the user's values (when automatic mode is off) → limits the
/// device itself reports (TjMax, NVML slowdown temperature, NVMe WCTEMP / CCTEMP) → documented vendor / device-class defaults.
/// Warning and critical limits are kept separate; a reported warning limit is never used as a critical one or vice versa.
/// </summary>
public static class ThresholdResolver
{
    public static IReadOnlyList<ComponentAlertRule> DefaultRules { get; } =
    [
        new() { Role = ComponentRole.CpuTemperature, Sustain = TimeSpan.FromSeconds(5) },
        new() { Role = ComponentRole.GpuTemperature, Sustain = TimeSpan.FromSeconds(5) },
        new() { Role = ComponentRole.GpuHotspot, Sustain = TimeSpan.FromSeconds(5) },
        new() { Role = ComponentRole.GpuMemoryTemperature, Sustain = TimeSpan.FromSeconds(10) },
        new() { Role = ComponentRole.StorageTemperature, Sustain = TimeSpan.FromSeconds(30) },
        new() { Role = ComponentRole.MemoryUtilization, Sustain = TimeSpan.FromSeconds(30), Hysteresis = 5 },
        new() { Role = ComponentRole.ThermalThrottling, Sustain = TimeSpan.FromSeconds(3), Hysteresis = 0 },
        new() { Role = ComponentRole.FanFailure, Sustain = TimeSpan.FromSeconds(10), Hysteresis = 0 },
    ];

    /// <summary>Sensors of a device that a component rule watches (live measurements only).</summary>
    public static IEnumerable<SensorReading> Targets(ComponentRole role, HardwareDevice device, IReadOnlyList<SensorReading> deviceSensors)
    {
        static bool Has(SensorReading s, string t) => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase);
        var live = deviceSensors.Where(s => s.IsMeasurement);
        return role switch
        {
            ComponentRole.CpuTemperature when device.Category == HardwareCategory.Cpu =>
                PickFirst(live.Where(s => s.Type == SensorType.Temperature), "CPU Package", "Core (Tctl/Tdie)", "Tctl", "Package", "Core Max", "Core Average"),
            ComponentRole.GpuTemperature when device.Category == HardwareCategory.Gpu =>
                PickFirst(live.Where(s => s.Type == SensorType.Temperature && !Has(s, "hot") && !Has(s, "memory") && !Has(s, "junction")), "GPU Core", "Core", "GPU Temperature", "Edge"),
            ComponentRole.GpuHotspot when device.Category == HardwareCategory.Gpu =>
                live.Where(s => s.Type == SensorType.Temperature && (Has(s, "hot spot") || Has(s, "hotspot"))).Take(1),
            ComponentRole.GpuMemoryTemperature when device.Category == HardwareCategory.Gpu =>
                live.Where(s => s.Type == SensorType.Temperature && (Has(s, "memory") || Has(s, "junction"))).Take(1),
            ComponentRole.StorageTemperature when device.Category == HardwareCategory.Storage =>
                PickFirst(live.Where(s => s.Type == SensorType.Temperature), "Composite Temperature", "Temperature"),
            ComponentRole.MemoryUtilization when device.Category == HardwareCategory.Memory =>
                PickFirst(live.Where(s => s.Type == SensorType.Utilization && !Has(s, "virtual") && !Has(s, "commit")), "Memory", "Physical Memory Load"),
            ComponentRole.ThermalThrottling =>
                live.Where(s => s.Type == SensorType.PerformanceLimit && Has(s, "thermal")),
            ComponentRole.FanFailure =>
                live.Where(s => s.Type == SensorType.FanSpeed),
            _ => [],
        };
    }

    private static IEnumerable<SensorReading> PickFirst(IEnumerable<SensorReading> candidates, params string[] preferred)
    {
        var list = candidates.ToList();
        foreach (var p in preferred)
        {
            var m = list.FirstOrDefault(s => s.Name.Equals(p, StringComparison.OrdinalIgnoreCase))
                 ?? list.FirstOrDefault(s => s.Name.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return [m];
        }

        return list.Take(1);
    }

    public static ResolvedThresholds Resolve(ComponentAlertRule rule, HardwareDevice device, IReadOnlyList<SensorReading> deviceSensors, SensorReading target)
    {
        if (!rule.UseAutomaticThresholds)
        {
            return new ResolvedThresholds(
                rule.WarningThreshold is { } w ? new ThresholdLevel(w, AlertKind.UserThreshold, "your threshold") : null,
                rule.CriticalThreshold is { } c ? new ThresholdLevel(c, AlertKind.UserThreshold, "your threshold") : null);
        }

        var vendor = VendorOf(device);
        var meta = deviceSensors.Where(s => s.Kind == SensorKind.Metadata && s.Value is > 0).ToList();
        double? Meta(params string[] words) => meta.FirstOrDefault(s => words.All(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))?.Value;

        switch (rule.Role)
        {
            case ComponentRole.CpuTemperature:
            {
                var tjmax = Meta("tjmax") ?? TjMaxFromDistance(deviceSensors);
                if (tjmax is { } tj)
                {
                    return new(new ThresholdLevel(tj - 10, AlertKind.WarningThreshold, $"auto: TjMax {tj:0} °C − 10"),
                        new ThresholdLevel(tj, AlertKind.DeviceReportedLimit, "TjMax reported by the CPU"));
                }

                return vendor switch
                {
                    "AMD" => Auto(85, 95, "AMD default (Ryzen Tjmax 95 °C)"),
                    "Intel" => Auto(90, 100, "Intel default (TjMax 100 °C)"),
                    _ => Auto(85, 95, "generic CPU default"),
                };
            }

            case ComponentRole.GpuTemperature:
            {
                if (Meta("slowdown") is { } slow)
                {
                    return new(new ThresholdLevel(slow - 5, AlertKind.WarningThreshold, $"auto: slowdown {slow:0} °C − 5"),
                        new ThresholdLevel(slow, AlertKind.DeviceReportedLimit, "slowdown temperature reported by the driver"));
                }

                return vendor switch
                {
                    "NVIDIA" => Auto(83, 90, "NVIDIA default"),
                    "AMD" => Auto(90, 100, "AMD default (edge temperature)"),
                    _ => Auto(85, 95, "generic GPU default"),
                };
            }

            case ComponentRole.GpuHotspot:
                return vendor == "AMD" ? Auto(100, 110, "AMD default (junction max 110 °C)") : Auto(95, 105, "default hotspot limits");

            case ComponentRole.GpuMemoryTemperature:
                return Auto(95, 105, "default memory-junction limits");

            case ComponentRole.StorageTemperature:
            {
                // NVMe WCTEMP / CCTEMP: warning and critical are distinct device limits.
                var wc = Meta("warning", "temperature");
                var cc = Meta("critical", "temperature");
                var solidState = device.Name.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || device.Name.Contains("SSD", StringComparison.OrdinalIgnoreCase);
                var fallback = solidState ? (70.0, 80.0, "SSD default") : (55.0, 60.0, "storage default (HDD-safe)");
                return new(
                    wc is { } w ? new ThresholdLevel(w, AlertKind.DeviceReportedLimit, "warning temperature reported by the drive") : new ThresholdLevel(fallback.Item1, AlertKind.WarningThreshold, fallback.Item3),
                    cc is { } c ? new ThresholdLevel(c, AlertKind.DeviceReportedLimit, "critical temperature reported by the drive") : new ThresholdLevel(fallback.Item2, AlertKind.CriticalThreshold, fallback.Item3));
            }

            case ComponentRole.MemoryUtilization:
                return Auto(90, 97, "memory pressure default");

            default:
                return new(null, null);
        }

        static ResolvedThresholds Auto(double w, double c, string source) =>
            new(new ThresholdLevel(w, AlertKind.WarningThreshold, $"auto: {source}"), new ThresholdLevel(c, AlertKind.CriticalThreshold, $"auto: {source}"));
    }

    /// <summary>Intel exposes "Distance to TjMax"; TjMax = temperature + distance for the same core.</summary>
    internal static double? TjMaxFromDistance(IReadOnlyList<SensorReading> sensors)
    {
        foreach (var d in sensors.Where(s => s.IsMeasurement && s.Type == SensorType.Temperature && s.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue))
        {
            var coreName = d.Name.Replace("Distance to TjMax", "", StringComparison.OrdinalIgnoreCase).Trim();
            var temp = sensors.FirstOrDefault(s => s.IsMeasurement && s.Type == SensorType.Temperature && s.Name.Equals(coreName, StringComparison.OrdinalIgnoreCase) && s.Value.HasValue);
            if (temp is not null) return Math.Round(temp.Value!.Value + d.Value!.Value);
        }

        return null;
    }

    public static string? VendorOf(HardwareDevice device)
    {
        if (device.Vendor is { } v) return v;
        if (device.Identity?.VendorId is { } id)
        {
            return id switch { 0x10DE => "NVIDIA", 0x1002 or 0x1022 => "AMD", 0x8086 => "Intel", _ => null };
        }

        var n = device.Name;
        if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase) || n.Contains("Core(TM)", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        return null;
    }
}
