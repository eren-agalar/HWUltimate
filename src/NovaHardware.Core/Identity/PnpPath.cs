using NovaHardware.Core.HardwareInfo;
using NovaHardware.Domain.Hardware;

namespace NovaHardware.Core.Identity;

/// <summary>Converts the many spellings of a PnP device path into one canonical instance id.</summary>
public static class PnpPath
{
    /// <summary>
    /// Accepts an instance id (<c>PCI\VEN_..\4&amp;..</c>) or a device-interface path
    /// (<c>\\?\pci#ven_..#4&amp;..#{guid}</c>) and returns the upper-case instance id, or null if it is not a device path.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            p = p[4..];
            var parts = p.Split('#');
            if (parts.Length < 3) return null;
            // enumerator#device-id#instance#{interface-guid}
            p = $"{parts[0]}\\{parts[1]}\\{parts[2]}";
        }

        p = p.Replace('/', '\\').ToUpperInvariant();
        return p.Contains('\\', StringComparison.Ordinal) && p.Contains("VEN_", StringComparison.Ordinal) ? p : null;
    }

    /// <summary>Builds an identity from a device path: canonical instance path plus PCI vendor / device / subsystem ids.</summary>
    public static DeviceIdentity FromPath(string? path)
    {
        var normalized = Normalize(path);
        var pci = PciIdentity.Parse(normalized ?? path);
        return new DeviceIdentity
        {
            InstancePath = normalized,
            VendorId = pci?.VendorId,
            DeviceId = pci?.DeviceId,
            SubsystemVendorId = pci?.SubsystemVendorId,
            SubsystemId = pci?.SubsystemId,
        };
    }
}
