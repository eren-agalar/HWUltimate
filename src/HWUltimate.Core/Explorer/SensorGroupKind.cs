using System.Text.RegularExpressions;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Core.Explorer;

/// <summary>Sensor category shown as an expandable group under each device.</summary>
public enum SensorGroupKind
{
    Temperatures = 0,
    Hotspot,
    MemoryTemperatures,
    Voltages,
    Currents,
    Powers,
    PowerLimits,
    Clocks,
    MemoryClocks,
    Utilization,
    PerCore,
    Throttling,
    PerformanceLimits,
    Fans,
    FanControl,
    Memory,
    Vram,
    Activity,
    Throughput,
    Data,
    Health,
    Levels,
    Energy,
    Timings,
    Other,
    /// <summary>Thresholds, limits and other static values reported through the sensor API (advanced view only).</summary>
    Metadata,
}

/// <summary>
/// Assigns sensors to explorer groups from their type, device category and provider-reported name.
/// Classification only affects presentation; values and units are never changed.
/// </summary>
public static partial class SensorClassifier
{
    [GeneratedRegex(@"\b(?:CPU\s+)?(?:P-|E-)?Core\s*#\s*\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PerCoreName();

    [GeneratedRegex(@"throttl|prochot|tjmax|distance to|thermal limit|power limit exceeded|limit reason", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThrottleName();

    public static bool IsPerCore(string name) => PerCoreName().IsMatch(name);

    public static SensorGroupKind Classify(SensorReading sensor, HardwareCategory category) =>
        sensor.Kind == SensorKind.Metadata ? SensorGroupKind.Metadata : Classify(sensor.Type, sensor.Name, category);

    public static SensorGroupKind Classify(SensorType type, string name, HardwareCategory category)
    {
        if (type == SensorType.PerformanceLimit)
        {
            return category == HardwareCategory.Cpu ? SensorGroupKind.Throttling : SensorGroupKind.PerformanceLimits;
        }

        if (category == HardwareCategory.Cpu && ThrottleName().IsMatch(name))
        {
            return SensorGroupKind.Throttling;
        }

        if (category == HardwareCategory.Cpu && IsPerCore(name) && type is SensorType.Temperature or SensorType.Clock or SensorType.Utilization or SensorType.Voltage or SensorType.Power or SensorType.Factor)
        {
            return SensorGroupKind.PerCore;
        }

        var lower = name.ToLowerInvariant();
        var isGpu = category == HardwareCategory.Gpu;
        return type switch
        {
            SensorType.Temperature when isGpu && (lower.Contains("hot spot", StringComparison.Ordinal) || lower.Contains("hotspot", StringComparison.Ordinal)) => SensorGroupKind.Hotspot,
            SensorType.Temperature when isGpu && (lower.Contains("memory", StringComparison.Ordinal) || lower.Contains("vram", StringComparison.Ordinal) || lower.Contains("junction", StringComparison.Ordinal)) => SensorGroupKind.MemoryTemperatures,
            SensorType.Temperature => SensorGroupKind.Temperatures,
            SensorType.Voltage => SensorGroupKind.Voltages,
            SensorType.Current => SensorGroupKind.Currents,
            SensorType.Power when lower.Contains("limit", StringComparison.Ordinal) => SensorGroupKind.PowerLimits,
            SensorType.Power => SensorGroupKind.Powers,
            SensorType.Clock when isGpu && lower.Contains("memory", StringComparison.Ordinal) => SensorGroupKind.MemoryClocks,
            SensorType.Clock or SensorType.Frequency => SensorGroupKind.Clocks,
            SensorType.Utilization when category == HardwareCategory.Storage && lower.Contains("activity", StringComparison.Ordinal) => SensorGroupKind.Activity,
            SensorType.Utilization => SensorGroupKind.Utilization,
            SensorType.FanSpeed or SensorType.Flow => SensorGroupKind.Fans,
            SensorType.FanControl => SensorGroupKind.FanControl,
            SensorType.Memory or SensorType.MemorySmall when isGpu => SensorGroupKind.Vram,
            SensorType.Memory or SensorType.MemorySmall when category == HardwareCategory.Storage || category == HardwareCategory.Network => SensorGroupKind.Data,
            SensorType.Memory or SensorType.MemorySmall => SensorGroupKind.Memory,
            SensorType.Throughput => SensorGroupKind.Throughput,
            SensorType.Level when category == HardwareCategory.Storage => SensorGroupKind.Health,
            SensorType.Level or SensorType.Factor or SensorType.Humidity or SensorType.Conductivity or SensorType.Noise => SensorGroupKind.Levels,
            SensorType.Energy => SensorGroupKind.Energy,
            SensorType.Duration or SensorType.Timing => SensorGroupKind.Timings,
            _ => SensorGroupKind.Other,
        };
    }
}
