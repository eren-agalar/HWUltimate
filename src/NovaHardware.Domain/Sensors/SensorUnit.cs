namespace NovaHardware.Domain.Sensors;

/// <summary>
/// Unit in which a sensor value is expressed. Values are always stored and reported
/// in the unit the provider supplied; Nova never silently rescales readings.
/// </summary>
public enum SensorUnit
{
    None = 0,
    Celsius,
    Volt,
    Ampere,
    Watt,
    Megahertz,
    Hertz,
    Percent,
    Rpm,
    LitersPerHour,
    Gigabyte,
    Megabyte,
    BytesPerSecond,
    MilliwattHour,
    DecibelA,
    MicrosiemensPerCentimeter,
    Second,
    Nanosecond,
    Ratio,
}

public static class SensorUnitExtensions
{
    public static string ToSymbol(this SensorUnit unit) => unit switch
    {
        SensorUnit.Celsius => "°C",
        SensorUnit.Volt => "V",
        SensorUnit.Ampere => "A",
        SensorUnit.Watt => "W",
        SensorUnit.Megahertz => "MHz",
        SensorUnit.Hertz => "Hz",
        SensorUnit.Percent => "%",
        SensorUnit.Rpm => "RPM",
        SensorUnit.LitersPerHour => "L/h",
        SensorUnit.Gigabyte => "GB",
        SensorUnit.Megabyte => "MB",
        SensorUnit.BytesPerSecond => "B/s",
        SensorUnit.MilliwattHour => "mWh",
        SensorUnit.DecibelA => "dBA",
        SensorUnit.MicrosiemensPerCentimeter => "µS/cm",
        SensorUnit.Second => "s",
        SensorUnit.Nanosecond => "ns",
        SensorUnit.Ratio => "×",
        _ => string.Empty,
    };

    /// <summary>Default unit for a sensor type when the provider does not state one explicitly.</summary>
    public static SensorUnit DefaultUnitFor(SensorType type) => type switch
    {
        SensorType.Temperature => SensorUnit.Celsius,
        SensorType.Voltage => SensorUnit.Volt,
        SensorType.Current => SensorUnit.Ampere,
        SensorType.Power => SensorUnit.Watt,
        SensorType.Clock => SensorUnit.Megahertz,
        SensorType.Utilization => SensorUnit.Percent,
        SensorType.FanSpeed => SensorUnit.Rpm,
        SensorType.FanControl => SensorUnit.Percent,
        SensorType.Flow => SensorUnit.LitersPerHour,
        SensorType.Level => SensorUnit.Percent,
        SensorType.Factor => SensorUnit.Ratio,
        SensorType.Memory => SensorUnit.Gigabyte,
        SensorType.MemorySmall => SensorUnit.Megabyte,
        SensorType.Throughput => SensorUnit.BytesPerSecond,
        SensorType.Frequency => SensorUnit.Hertz,
        SensorType.Energy => SensorUnit.MilliwattHour,
        SensorType.Noise => SensorUnit.DecibelA,
        SensorType.Humidity => SensorUnit.Percent,
        SensorType.Conductivity => SensorUnit.MicrosiemensPerCentimeter,
        SensorType.Duration => SensorUnit.Second,
        SensorType.Timing => SensorUnit.Nanosecond,
        _ => SensorUnit.None,
    };
}
