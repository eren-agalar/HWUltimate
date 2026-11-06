using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Domain.Alerts;

public enum AlertSeverity { Information = 0, Warning, Critical }

public enum AlertComparison { Above = 0, Below }

/// <summary>What kind of condition produced an alert. Each kind has its own wording; they are never conflated.</summary>
public enum AlertKind
{
    /// <summary>A warning threshold (automatic, component-specific) was exceeded.</summary>
    WarningThreshold = 0,
    /// <summary>A critical threshold (automatic, component-specific) was exceeded.</summary>
    CriticalThreshold,
    /// <summary>The device's own reported limit (e.g. NVML slowdown temperature, SMART critical temperature) was reached.</summary>
    DeviceReportedLimit,
    /// <summary>A value crossed a threshold the user configured.</summary>
    UserThreshold,
    /// <summary>The hardware reports active thermal / power throttling.</summary>
    Throttling,
    /// <summary>A fan that had been spinning stopped.</summary>
    FanFailure,
}

/// <summary>Which component a built-in rule watches. Thresholds are resolved per device, not globally.</summary>
public enum ComponentRole
{
    CpuTemperature = 0,
    GpuTemperature,
    GpuHotspot,
    GpuMemoryTemperature,
    StorageTemperature,
    MemoryUtilization,
    ThermalThrottling,
    FanFailure,
}

/// <summary>
/// A built-in, component-aware rule. In <see cref="UseAutomaticThresholds"/> mode, thresholds come from the device
/// (reported limits) or from documented vendor / device-class defaults; otherwise the user's values apply.
/// </summary>
public sealed record ComponentAlertRule
{
    public required ComponentRole Role { get; init; }
    public bool Enabled { get; init; } = true;
    public bool UseAutomaticThresholds { get; init; } = true;
    public double? WarningThreshold { get; init; }
    public double? CriticalThreshold { get; init; }
    /// <summary>Value must fall this far below the threshold before the alert clears.</summary>
    public double Hysteresis { get; init; } = 3;
    /// <summary>Condition must hold continuously this long before the alert fires.</summary>
    public TimeSpan Sustain { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Minimum time before the same alert can fire again after it cleared.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Stable id derived from the role, so built-in rules keep their identity across restarts.</summary>
    public Guid Id => Guid.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"00000000-0000-4000-8000-{(int)Role + 1:D12}"));
}

/// <summary>
/// A user-defined threshold rule. A rule targets either a specific <see cref="SensorId"/>, or all sensors of
/// <see cref="SensorType"/> (optionally restricted to a <see cref="DeviceCategory"/> and a name filter).
/// </summary>
public sealed record AlertRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public string? SensorId { get; init; }
    public SensorType? SensorType { get; init; }
    public HardwareCategory? DeviceCategory { get; init; }
    public string? NameContains { get; init; }
    public AlertComparison Comparison { get; init; } = AlertComparison.Above;
    public double Threshold { get; init; }
    public double Hysteresis { get; init; } = 2;
    public TimeSpan Sustain { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(5);
    public AlertSeverity Severity { get; init; } = AlertSeverity.Warning;
}

public enum AlertState { Raised = 0, Cleared }

public sealed record AlertEvent(
    Guid RuleId,
    string RuleName,
    AlertSeverity Severity,
    AlertState State,
    AlertKind Kind,
    string SensorId,
    string SensorName,
    string DeviceName,
    double Value,
    double Threshold,
    SensorUnit Unit,
    DateTimeOffset Timestamp,
    string? ThresholdSource = null)
{
    private string V(double v) => Unit.FormatValue(v);

    public string Message => State == AlertState.Cleared
        ? $"{DeviceName} · {SensorName} returned to normal ({V(Value)})."
        : Kind switch
        {
            AlertKind.WarningThreshold => $"{DeviceName} · {SensorName} is {V(Value)} — above the warning threshold of {V(Threshold)}{Src}.",
            AlertKind.CriticalThreshold => $"{DeviceName} · {SensorName} is {V(Value)} — above the critical threshold of {V(Threshold)}{Src}.",
            AlertKind.DeviceReportedLimit => $"{DeviceName} · {SensorName} is {V(Value)} — at or above the limit reported by the device ({V(Threshold)}{Src}).",
            AlertKind.UserThreshold => $"{DeviceName} · {SensorName} is {V(Value)} — crossed your threshold of {V(Threshold)}.",
            AlertKind.Throttling => $"{DeviceName} reports active {SensorName.ToLowerInvariant()}.",
            AlertKind.FanFailure => $"{DeviceName} · {SensorName} stopped (0 RPM) after spinning earlier in this session.",
            _ => $"{DeviceName} · {SensorName}: {V(Value)}",
        };

    private string Src => string.IsNullOrEmpty(ThresholdSource) ? "" : $"; {ThresholdSource}";
}
