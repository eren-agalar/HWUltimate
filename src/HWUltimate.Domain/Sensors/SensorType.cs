namespace HWUltimate.Domain.Sensors;

/// <summary>Physical or logical quantity a sensor measures.</summary>
public enum SensorType
{
    Other = 0,
    Temperature,
    Voltage,
    Current,
    Power,
    Clock,
    Utilization,
    FanSpeed,
    FanControl,
    Flow,
    Level,
    Factor,
    Memory,
    MemorySmall,
    Throughput,
    Frequency,
    Energy,
    Noise,
    Humidity,
    Conductivity,
    Duration,
    Timing,
    /// <summary>A boolean limiter / throttle flag (1 = active, 0 = inactive), e.g. NVML clock-event reasons.</summary>
    PerformanceLimit,
}
