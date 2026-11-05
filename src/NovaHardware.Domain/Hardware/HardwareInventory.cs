namespace NovaHardware.Domain.Hardware;

/// <summary>Whether a property value was actually obtained from the system.</summary>
public enum PropertyStatus
{
    /// <summary>Value read from a system API, firmware table, driver or vendor SDK.</summary>
    Available = 0,
    /// <summary>The platform / hardware does not expose this property. <see cref="HardwareProperty.Value"/> is "Not Available".</summary>
    NotAvailable,
    /// <summary>The property exists but could not be read (e.g. requires administrator rights).</summary>
    RequiresElevation,
}

/// <summary>Standard presentation sections for device properties.</summary>
public static class PropertySections
{
    public const string Overview = "Overview";
    public const string Identification = "Identification";
    public const string Specifications = "Technical Specifications";
    public const string Performance = "Performance Capabilities";
    public const string Firmware = "Firmware & Drivers";
    public const string Advanced = "Advanced Details";

    public static IReadOnlyList<string> All { get; } = [Overview, Identification, Specifications, Performance, Firmware, Advanced];
}

/// <summary>
/// A named property of a device, e.g. "Cores" = "16". <see cref="Source"/> names the API the value came from
/// (e.g. "CPUID", "SMBIOS type 17", "NVML"). Unsupported properties are listed explicitly with
/// <see cref="PropertyStatus.NotAvailable"/> rather than being omitted or guessed.
/// </summary>
public sealed record HardwareProperty(
    string Name,
    string Value,
    string? Group = null,
    string? Source = null,
    PropertyStatus Status = PropertyStatus.Available,
    string? Note = null)
{
    public const string NotAvailableText = "Not Available";

    public static HardwareProperty Unavailable(string name, string group, string note, string? checkedSources = null) =>
        new(name, NotAvailableText, group, checkedSources, PropertyStatus.NotAvailable, note);

    public bool IsAvailable => Status == PropertyStatus.Available;
}

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
