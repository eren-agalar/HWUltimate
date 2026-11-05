using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Tests;

internal sealed class FakeProvider : IHardwareProvider
{
    public FakeProvider(string name, int priority = 0) { Name = name; Priority = priority; }

    public string Name { get; }
    public int Priority { get; }
    public ProviderStatus Status { get; set; } = ProviderStatus.Ready();
    public List<HardwareDevice> Devices { get; } = [];
    public List<RawSensorReading> Readings { get; set; } = [];
    public bool ThrowOnUpdate { get; set; }
    public int Updates { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Update() { Updates++; if (ThrowOnUpdate) throw new InvalidOperationException("boom"); }
    public IReadOnlyList<RawSensorReading> ReadSensors() => Readings;
    public IReadOnlyList<HardwareDevice> GetDevices() => Devices;
    public void Dispose() { }

    public FakeProvider WithDevice(string id, HardwareCategory category, string? name = null)
    {
        Devices.Add(new HardwareDevice(id, name ?? id, category, Name));
        return this;
    }

    public FakeProvider Set(string sensorId, string deviceId, string name, SensorType type, double? value)
    {
        Readings.RemoveAll(r => r.SensorId == sensorId);
        Readings.Add(new RawSensorReading(sensorId, deviceId, name, type, SensorUnitExtensions.DefaultUnitFor(type), value, Name));
        return this;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => _now += by;
}

internal static class Snapshots
{
    public static SensorReading Reading(string id, string device, string name, SensorType type, double? value,
        DataQuality quality = DataQuality.Good, DateTimeOffset? ts = null) => new()
    {
        SensorId = id,
        DeviceId = device,
        Name = name,
        Type = type,
        Unit = SensorUnitExtensions.DefaultUnitFor(type),
        Value = value,
        Timestamp = ts ?? DateTimeOffset.Now,
        ProviderName = "test",
        Quality = quality,
    };

    public static SensorSnapshot Of(DateTimeOffset ts, IEnumerable<HardwareDevice> devices, params SensorReading[] sensors) =>
        new(ts, devices.ToList(), sensors.Select(s => s with { Timestamp = ts }).ToList(), TimeSpan.Zero);
}
