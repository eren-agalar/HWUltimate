using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using NovaHardware.Domain.Stress;

namespace NovaHardware.Domain.Reports;

public enum ReportFormat { Html = 0, Json, Text, Csv }

[Flags]
public enum ReportSections
{
    None = 0,
    Summary = 1,
    Devices = 2,
    Sensors = 4,
    Alerts = 8,
    StressTests = 16,
    All = Summary | Devices | Sensors | Alerts | StressTests,
}

public sealed record ReportData
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public string ApplicationVersion { get; init; } = "0.0.0";
    public ReportSections Sections { get; init; } = ReportSections.All;
    public HardwareInventory Inventory { get; init; } = HardwareInventory.Empty;
    public SensorSnapshot Sensors { get; init; } = SensorSnapshot.Empty;
    public IReadOnlyList<AlertEvent> Alerts { get; init; } = [];
    public IReadOnlyList<StressTestResult> StressTests { get; init; } = [];
}
