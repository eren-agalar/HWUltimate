using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using Lhm = LibreHardwareMonitor.Hardware;

namespace NovaHardware.Hardware;

/// <summary>Maps LibreHardwareMonitor enums onto Nova's unified model, preserving LHM's native units.</summary>
internal static class LibreHardwareMonitorMapping
{
    public static (SensorType Type, SensorUnit Unit) Map(Lhm.SensorType type) => type switch
    {
        Lhm.SensorType.Voltage => (SensorType.Voltage, SensorUnit.Volt),
        Lhm.SensorType.Current => (SensorType.Current, SensorUnit.Ampere),
        Lhm.SensorType.Power => (SensorType.Power, SensorUnit.Watt),
        Lhm.SensorType.Clock => (SensorType.Clock, SensorUnit.Megahertz),
        Lhm.SensorType.Temperature => (SensorType.Temperature, SensorUnit.Celsius),
        Lhm.SensorType.Load => (SensorType.Utilization, SensorUnit.Percent),
        Lhm.SensorType.Frequency => (SensorType.Frequency, SensorUnit.Hertz),
        Lhm.SensorType.Fan => (SensorType.FanSpeed, SensorUnit.Rpm),
        Lhm.SensorType.Flow => (SensorType.Flow, SensorUnit.LitersPerHour),
        Lhm.SensorType.Control => (SensorType.FanControl, SensorUnit.Percent),
        Lhm.SensorType.Level => (SensorType.Level, SensorUnit.Percent),
        Lhm.SensorType.Factor => (SensorType.Factor, SensorUnit.Ratio),
        Lhm.SensorType.Data => (SensorType.Memory, SensorUnit.Gigabyte),
        Lhm.SensorType.SmallData => (SensorType.MemorySmall, SensorUnit.Megabyte),
        Lhm.SensorType.Throughput => (SensorType.Throughput, SensorUnit.BytesPerSecond),
        Lhm.SensorType.TimeSpan => (SensorType.Duration, SensorUnit.Second),
        Lhm.SensorType.Timing => (SensorType.Timing, SensorUnit.Nanosecond),
        Lhm.SensorType.Energy => (SensorType.Energy, SensorUnit.MilliwattHour),
        Lhm.SensorType.Noise => (SensorType.Noise, SensorUnit.DecibelA),
        Lhm.SensorType.Conductivity => (SensorType.Conductivity, SensorUnit.MicrosiemensPerCentimeter),
        Lhm.SensorType.Humidity => (SensorType.Humidity, SensorUnit.Percent),
        _ => (SensorType.Other, SensorUnit.None),
    };

    public static HardwareCategory Map(Lhm.HardwareType type, string? name = null) => type switch
    {
        Lhm.HardwareType.Memory when name is not null && name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase) => HardwareCategory.MemoryModule,
        Lhm.HardwareType.Cpu => HardwareCategory.Cpu,
        Lhm.HardwareType.GpuNvidia or Lhm.HardwareType.GpuAmd or Lhm.HardwareType.GpuIntel => HardwareCategory.Gpu,
        Lhm.HardwareType.Motherboard => HardwareCategory.Motherboard,
        Lhm.HardwareType.SuperIO => HardwareCategory.SuperIo,
        Lhm.HardwareType.EmbeddedController => HardwareCategory.EmbeddedController,
        Lhm.HardwareType.Memory => HardwareCategory.Memory,
        Lhm.HardwareType.Storage => HardwareCategory.Storage,
        Lhm.HardwareType.Network => HardwareCategory.Network,
        Lhm.HardwareType.Battery => HardwareCategory.Battery,
        Lhm.HardwareType.Psu => HardwareCategory.PowerSupply,
        Lhm.HardwareType.Cooler => HardwareCategory.Cooler,
        _ => HardwareCategory.Other,
    };

    public static string? Vendor(Lhm.HardwareType type) => type switch
    {
        Lhm.HardwareType.GpuNvidia => "NVIDIA",
        Lhm.HardwareType.GpuAmd => "AMD",
        Lhm.HardwareType.GpuIntel => "Intel",
        _ => null,
    };
}
