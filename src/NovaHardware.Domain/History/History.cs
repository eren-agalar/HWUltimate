using NovaHardware.Domain.Sensors;

namespace NovaHardware.Domain.History;

/// <summary>A persisted, real sample of one sensor.</summary>
public readonly record struct SensorSample(string SensorId, DateTimeOffset Timestamp, double Value);

/// <summary>Metadata about a sensor that has history.</summary>
public sealed record SensorDescriptor(
    string SensorId,
    string DeviceId,
    string DeviceName,
    string Name,
    SensorType Type,
    SensorUnit Unit,
    string ProviderName);

/// <summary>Aggregated bucket for long time ranges. Every bucket is built from real samples only.</summary>
public readonly record struct AggregatedSample(
    DateTimeOffset BucketStart,
    double Minimum,
    double Maximum,
    double Average,
    int Count);

public sealed record HistoryQuery(
    string SensorId,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? BucketSize = null);

public sealed record HistoryStatistics(
    string SensorId,
    double Minimum,
    double Maximum,
    double Average,
    long Count,
    DateTimeOffset? First,
    DateTimeOffset? Last);
