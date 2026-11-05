namespace NovaHardware.Domain.Sensors;

/// <summary>
/// A single raw value as produced by a hardware provider during one poll.
/// <see cref="Value"/> is <c>null</c> when the provider has no value — providers must never fabricate one.
/// </summary>
public sealed record RawSensorReading(
    string SensorId,
    string DeviceId,
    string Name,
    SensorType Type,
    SensorUnit Unit,
    double? Value,
    string ProviderName,
    SensorAvailability Availability = SensorAvailability.Available,
    int Index = 0);

/// <summary>
/// Unified, enriched sensor model exposed to the rest of the application.
/// Min/Max/Average are computed by Nova over the current monitoring session from real samples only.
/// </summary>
public sealed record SensorReading
{
    public required string SensorId { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required SensorType Type { get; init; }
    public required SensorUnit Unit { get; init; }
    public double? Value { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? Average { get; init; }
    public long SampleCount { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ProviderName { get; init; }
    public SensorAvailability Availability { get; init; }
    public DataQuality Quality { get; init; }
    public int Index { get; init; }

    public string UnitSymbol => Unit.ToSymbol();
    public bool HasValue => Value.HasValue;
}
