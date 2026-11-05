namespace NovaHardware.Domain.Stress;

public enum StressTestKind
{
    CpuFloatingPoint = 0,
    CpuInteger,
    CpuVector,
    Memory,
    Gpu,
}

public sealed record StressTestOptions
{
    public required StressTestKind Kind { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Worker threads; 0 = all logical processors.</summary>
    public int Threads { get; init; }
    /// <summary>Memory test size in MiB; 0 = automatic (portion of available memory).</summary>
    public int MemoryMegabytes { get; init; }
    /// <summary>Abort if any CPU/GPU temperature sensor exceeds this value in °C. 0 disables.</summary>
    public double ThermalLimitCelsius { get; init; } = 95;
    /// <summary>Stop the test on the first computational error.</summary>
    public bool StopOnError { get; init; } = true;
}

public enum StressTestOutcome
{
    Passed = 0,
    Failed,
    Cancelled,
    AbortedThermal,
    NotSupported,
    Error,
}

public sealed record StressTestProgress(
    TimeSpan Elapsed,
    TimeSpan Duration,
    long Iterations,
    long Errors,
    double IterationsPerSecond,
    double? MaxTemperature,
    string? Message)
{
    public double Fraction => Duration <= TimeSpan.Zero ? 0 : Math.Clamp(Elapsed / Duration, 0, 1);
}

public sealed record StressTestResult(
    StressTestKind Kind,
    StressTestOutcome Outcome,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    long Iterations,
    long Errors,
    double? MaxTemperature,
    IReadOnlyList<string> ErrorDetails,
    string Summary);
