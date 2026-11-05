using NovaHardware.Domain.Hardware;

namespace NovaHardware.Domain.Sensors;

/// <summary>Immutable result of a complete polling cycle across all providers.</summary>
public sealed class SensorSnapshot
{
    public static SensorSnapshot Empty { get; } = new(DateTimeOffset.MinValue, [], [], TimeSpan.Zero);

    public SensorSnapshot(
        DateTimeOffset timestamp,
        IReadOnlyList<HardwareDevice> devices,
        IReadOnlyList<SensorReading> sensors,
        TimeSpan pollDuration)
    {
        Timestamp = timestamp;
        Devices = devices;
        Sensors = sensors;
        PollDuration = pollDuration;
        _byId = sensors.GroupBy(s => s.SensorId, StringComparer.Ordinal)
                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private readonly Dictionary<string, SensorReading> _byId;

    public DateTimeOffset Timestamp { get; }
    public IReadOnlyList<HardwareDevice> Devices { get; }
    public IReadOnlyList<SensorReading> Sensors { get; }
    public TimeSpan PollDuration { get; }

    public SensorReading? Find(string sensorId) => _byId.GetValueOrDefault(sensorId);

    public IEnumerable<SensorReading> ForDevice(string deviceId) =>
        Sensors.Where(s => string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal));

    public IEnumerable<HardwareDevice> DevicesOf(HardwareCategory category) =>
        Devices.Where(d => d.Category == category);
}
