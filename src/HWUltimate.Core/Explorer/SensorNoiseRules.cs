using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Core.Explorer;

/// <summary>
/// Decides which sensors are "secondary": real but rarely useful, hidden from the default view and shown with
/// "Show all sensors". Rules are generic (sensor type, device class, observed values) — never model names — so they
/// behave the same on every machine. Nothing is deleted or changed; values stay exactly as reported.
/// </summary>
public static class SensorNoiseRules
{
    public static bool IsMotherboardChip(HardwareCategory c) =>
        c is HardwareCategory.Motherboard or HardwareCategory.SuperIo or HardwareCategory.EmbeddedController;

    public static bool IsSecondary(SensorReading s, HardwareCategory deviceCategory, SensorGroupKind group)
    {
        // Stale readings belong to a provider that just failed; keep them visible so the problem is noticed.
        if (s.Quality == DataQuality.Stale) return false;

        // Never produced a value this session (input not wired, feature unsupported by this device).
        if (s.SampleCount == 0 && s.Value is null) return true;

        if (IsMotherboardChip(deviceCategory))
        {
            switch (s.Type)
            {
                // Empty fan header: has never turned. (GPU fans are not affected: zero-RPM idle is normal there.)
                case SensorType.FanSpeed when s.Maximum is <= 0:
                    return true;
                // Unconnected voltage input.
                case SensorType.Voltage when s.Maximum is <= 0:
                    return true;
                // Unconnected thermistor input: out of physical range, or never above 0 °C.
                case SensorType.Temperature when s.Quality == DataQuality.Suspect || s.Maximum is <= 0:
                    return true;
            }
        }

        // "Distance to TjMax" per core is TjMax minus the core temperature that is already listed.
        if (deviceCategory == HardwareCategory.Cpu && s.Name.Contains("distance to", StringComparison.OrdinalIgnoreCase)) return true;

        // Cumulative network traffic counters; speeds and utilisation stay visible.
        if (deviceCategory == HardwareCategory.Network && group == SensorGroupKind.Data) return true;

        return false;
    }
}
