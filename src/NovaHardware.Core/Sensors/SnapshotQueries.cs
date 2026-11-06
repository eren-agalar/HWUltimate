using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Sensors;

/// <summary>
/// Helpers for picking "headline" sensors (e.g. CPU package temperature) out of a snapshot.
/// Selection is by name heuristics across providers; if nothing matches, <c>null</c> is returned — never a guess.
/// </summary>
public static class SnapshotQueries
{
    public static SensorReading? Pick(
        this SensorSnapshot snapshot,
        HardwareCategory category,
        SensorType type,
        params string[] preferredNames)
    {
        var deviceIds = snapshot.DevicesOf(category).Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
        var candidates = snapshot.Sensors
            .Where(s => s.Type == type && s.IsMeasurement && deviceIds.Contains(s.DeviceId) && s.Value.HasValue)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var name in preferredNames)
        {
            var match = candidates.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                     ?? candidates.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return candidates[0];
    }

    public static SensorReading? CpuTemperature(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Tctl", "Package", "Core Average", "Core Max");

    public static SensorReading? CpuLoad(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Cpu, SensorType.Utilization, "CPU Total", "Total");

    public static SensorReading? CpuPower(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Cpu, SensorType.Power, "CPU Package", "Package");

    public static SensorReading? CpuClock(this SensorSnapshot s)
    {
        var deviceIds = s.DevicesOf(HardwareCategory.Cpu).Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
        var cores = s.Sensors.Where(x => x.Type == SensorType.Clock && x.IsMeasurement && deviceIds.Contains(x.DeviceId)
                                        && x.Value.HasValue && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)).ToList();
        return cores.Count == 0 ? null : cores.MaxBy(x => x.Value);
    }

    public static SensorReading? GpuTemperature(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Gpu, SensorType.Temperature, "GPU Core", "Core", "GPU Hot Spot");

    public static SensorReading? GpuLoad(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Gpu, SensorType.Utilization, "GPU Core", "D3D 3D", "Core");

    public static SensorReading? GpuPower(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Gpu, SensorType.Power, "GPU Package", "GPU Power", "Total");

    public static SensorReading? GpuClock(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Gpu, SensorType.Clock, "GPU Core", "Core", "Graphics");

    public static SensorReading? MemoryLoad(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Memory, SensorType.Utilization, "Memory");

    public static SensorReading? MemoryUsed(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Memory, SensorType.Memory, "Memory Used", "Used");

    public static SensorReading? MemoryAvailable(this SensorSnapshot s) =>
        s.Pick(HardwareCategory.Memory, SensorType.Memory, "Memory Available", "Available");

    /// <summary>Highest temperature among CPU and GPU sensors with good data.</summary>
    public static double? MaxProcessorTemperature(this SensorSnapshot s)
    {
        var ids = s.Devices.Where(d => d.Category is HardwareCategory.Cpu or HardwareCategory.Gpu)
                           .Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
        var temps = s.Sensors.Where(x => x.Type == SensorType.Temperature && x.IsMeasurement && x.Unit == SensorUnit.Celsius
                                         && ids.Contains(x.DeviceId) && x.Value.HasValue && x.Quality == DataQuality.Good)
                             .Select(x => x.Value!.Value).ToList();
        return temps.Count == 0 ? null : temps.Max();
    }
}
