namespace NovaHardware.Domain.Sensors;

/// <summary>
/// Broad physical plausibility ranges used only to flag suspicious values.
/// Values outside these ranges are never altered or discarded — only marked <see cref="DataQuality.Suspect"/>.
/// </summary>
public static class SensorPlausibility
{
    public static (double Min, double Max)? RangeFor(SensorType type, SensorUnit unit) => (type, unit) switch
    {
        (SensorType.Temperature, SensorUnit.Celsius) => (-60, 200),
        (SensorType.Utilization, SensorUnit.Percent) => (0, 100.5),
        (SensorType.Level, SensorUnit.Percent) => (0, 100.5),
        (SensorType.FanControl, SensorUnit.Percent) => (0, 100.5),
        (SensorType.Humidity, SensorUnit.Percent) => (0, 100.5),
        (SensorType.Voltage, SensorUnit.Volt) => (-15, 60),
        (SensorType.FanSpeed, SensorUnit.Rpm) => (0, 30000),
        (SensorType.Clock, SensorUnit.Megahertz) => (0, 20000),
        (SensorType.Power, SensorUnit.Watt) => (-5, 5000),
        _ => null,
    };

    public static bool IsPlausible(SensorType type, SensorUnit unit, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        var range = RangeFor(type, unit);
        return range is null || (value >= range.Value.Min && value <= range.Value.Max);
    }
}
