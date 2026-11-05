namespace NovaHardware.Domain.Hardware;

/// <summary>A named property of a device, e.g. "Cores" = "16".</summary>
public sealed record HardwareProperty(string Name, string Value, string? Group = null);

/// <summary>Identification record for a discovered device (WMI / SMBIOS / SetupAPI).</summary>
public sealed record DeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required HardwareCategory Category { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? DriverVersion { get; init; }
    public required string Source { get; init; }
    public IReadOnlyList<HardwareProperty> Properties { get; init; } = [];
}

/// <summary>High-level overview of the machine.</summary>
public sealed record SystemSummary
{
    public string ComputerName { get; init; } = Environment.MachineName;
    public string? OperatingSystem { get; init; }
    public string? OsVersion { get; init; }
    public string? OsBuild { get; init; }
    public string? SystemManufacturer { get; init; }
    public string? SystemModel { get; init; }
    public string? Motherboard { get; init; }
    public string? BiosVersion { get; init; }
    public string? BiosDate { get; init; }
    public string? Processor { get; init; }
    public int? PhysicalCores { get; init; }
    public int? LogicalProcessors { get; init; }
    public ulong? TotalMemoryBytes { get; init; }
    public IReadOnlyList<string> Gpus { get; init; } = [];
    public TimeSpan? Uptime { get; init; }
}

public sealed record HardwareInventory(
    DateTimeOffset CollectedAt,
    SystemSummary Summary,
    IReadOnlyList<DeviceInfo> Devices,
    IReadOnlyList<string> Warnings)
{
    public static HardwareInventory Empty { get; } = new(DateTimeOffset.MinValue, new SystemSummary(), [], []);

    public IEnumerable<DeviceInfo> OfCategory(HardwareCategory category) => Devices.Where(d => d.Category == category);
}
