using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Domain.Reports;

public enum ReportFormat { Html = 0, Json, Csv, Text }

[Flags]
public enum ReportSections
{
    None = 0,
    Summary = 1,
    Inventory = 2,
    Cpu = 4,
    Gpu = 8,
    Memory = 16,
    Storage = 32,
    Sensors = 64,
    All = Summary | Inventory | Cpu | Gpu | Memory | Storage | Sensors,
}

public sealed record ReportData
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public string ApplicationVersion { get; init; } = "0.0.0";
    public ReportSections Sections { get; init; } = ReportSections.All;
    public HardwareInventory Inventory { get; init; } = HardwareInventory.Empty;
    public SensorSnapshot Sensors { get; init; } = SensorSnapshot.Empty;
}
