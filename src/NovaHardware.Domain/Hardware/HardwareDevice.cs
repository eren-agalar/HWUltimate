namespace NovaHardware.Domain.Hardware;

/// <summary>A sensor-bearing device reported by a hardware provider.</summary>
public sealed record HardwareDevice(
    string DeviceId,
    string Name,
    HardwareCategory Category,
    string ProviderName,
    string? ParentDeviceId = null,
    string? Vendor = null);
