using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Domain.Alerts;

public enum AlertSeverity { Information = 0, Warning, Critical }

public enum AlertComparison { Above = 0, Below }

/// <summary>
/// A threshold rule. A rule targets either a specific <see cref="SensorId"/>, or all sensors of
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
    /// <summary>Value must return this far past the threshold before the alert clears.</summary>
    public double Hysteresis { get; init; } = 2;
    /// <summary>Condition must hold continuously this long before the alert fires.</summary>
    public TimeSpan Sustain { get; init; } = TimeSpan.FromSeconds(5);
    public AlertSeverity Severity { get; init; } = AlertSeverity.Warning;
}

public enum AlertState { Raised = 0, Cleared }

public sealed record AlertEvent(
    Guid RuleId,
    string RuleName,
    AlertSeverity Severity,
    AlertState State,
    string SensorId,
    string SensorName,
    string DeviceName,
    double Value,
    double Threshold,
    SensorUnit Unit,
    DateTimeOffset Timestamp)
{
    public string Message =>
        $"{DeviceName} · {SensorName}: {Value:0.##} {Unit.ToSymbol()} " +
        (State == AlertState.Raised ? $"crossed {Threshold:0.##} {Unit.ToSymbol()}" : "returned to normal");
}
