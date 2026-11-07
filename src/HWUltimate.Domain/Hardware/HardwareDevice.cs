using System.Globalization;

namespace HWUltimate.Domain.Hardware;

/// <summary>PCI location (segment:bus:device.function). Unique per physical PCI function in a running system.</summary>
public readonly record struct PciLocation(int Segment, int Bus, int Device, int Function)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Segment:X4}:{Bus:X2}:{Device:X2}.{Function:X}");
}

/// <summary>
/// What a provider knows about the physical identity of a device. Used to merge the views several providers
/// have of the same hardware (e.g. LibreHardwareMonitor + NVAPI + NVML for one GPU) without ever merging two
/// different physical devices. All members are optional; providers fill in what they can verify.
/// </summary>
public sealed record DeviceIdentity
{
    /// <summary>Normalised PnP device instance id, e.g. <c>PCI\VEN_10DE&amp;DEV_2684&amp;SUBSYS_16F110DE&amp;REV_A1\4&amp;2283F625&amp;0&amp;0019</c>.</summary>
    public string? InstancePath { get; init; }
    public PciLocation? Location { get; init; }
    public ushort? VendorId { get; init; }
    public ushort? DeviceId { get; init; }
    public ushort? SubsystemVendorId { get; init; }
    public ushort? SubsystemId { get; init; }
    /// <summary>For logical devices of which a system has exactly one (e.g. "system-memory").</summary>
    public string? SingletonKey { get; init; }

    public bool IsEmpty => InstancePath is null && Location is null && VendorId is null && SingletonKey is null;

    /// <summary>"10DE:2684 / 10DE:16F1" style id string, or null when the PCI ids are unknown.</summary>
    public string? PciIdText => VendorId is { } v && DeviceId is { } d
        ? string.Create(CultureInfo.InvariantCulture, $"{v:X4}:{d:X4}") + (SubsystemVendorId is { } sv && SubsystemId is { } sd ? string.Create(CultureInfo.InvariantCulture, $" / {sv:X4}:{sd:X4}") : "")
        : null;
}

/// <summary>A sensor-bearing device. After merging, one instance represents one physical device.</summary>
public sealed record HardwareDevice(
    string DeviceId,
    string Name,
    HardwareCategory Category,
    string ProviderName,
    string? ParentDeviceId = null,
    string? Vendor = null,
    DeviceIdentity? Identity = null)
{
    /// <summary>Every provider contributing sensors to this (merged) device.</summary>
    public IReadOnlyList<string> Providers { get; init; } = [ProviderName];

    /// <summary>The provider-specific device ids merged into this device.</summary>
    public IReadOnlyList<string> SourceDeviceIds { get; init; } = [DeviceId];
}
