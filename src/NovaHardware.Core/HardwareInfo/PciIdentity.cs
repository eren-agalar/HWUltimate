using System.Globalization;
using System.Text.RegularExpressions;

namespace NovaHardware.Core.HardwareInfo;

/// <summary>PCI identity parsed from a Windows PnP device instance id such as
/// <c>PCI\VEN_10DE&amp;DEV_2684&amp;SUBSYS_16F110DE&amp;REV_A1\4&amp;...</c>.</summary>
public sealed partial record PciIdentity(ushort VendorId, ushort DeviceId, ushort? SubsystemVendorId, ushort? SubsystemId, byte? Revision)
{
    [GeneratedRegex(@"VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})(?:&SUBSYS_(?<sub>[0-9A-F]{8}))?(?:&REV_(?<rev>[0-9A-F]{2}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static PciIdentity? Parse(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return null;
        var m = Pattern().Match(pnpDeviceId);
        if (!m.Success) return null;
        static ushort H16(string s) => ushort.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ushort? subVen = null, subDev = null;
        if (m.Groups["sub"].Success)
        {
            var sub = m.Groups["sub"].Value; // SUBSYS = subsystem device id (high word) + subsystem vendor id (low word)
            subDev = H16(sub[..4]);
            subVen = H16(sub[4..]);
        }

        byte? rev = m.Groups["rev"].Success ? byte.Parse(m.Groups["rev"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
        return new PciIdentity(H16(m.Groups["ven"].Value), H16(m.Groups["dev"].Value), subVen, subDev, rev);
    }

    public string VendorName => VendorNameOf(VendorId);

    public static string VendorNameOf(ushort vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x15AD => "VMware",
        0x80EE => "Oracle VirtualBox",
        0x1AF4 => "Red Hat (virtio)",
        0x5143 => "Qualcomm",
        0x1043 => "ASUS",
        0x1458 => "Gigabyte",
        0x1462 => "MSI",
        0x3842 => "EVGA",
        0x19DA => "Zotac",
        0x1569 => "Palit",
        0x1DA2 => "Sapphire",
        0x148C => "PowerColor",
        0x1682 => "XFX",
        0x1849 => "ASRock",
        0x10B0 => "Gainward",
        0x196E => "PNY",
        0x103C => "HP",
        0x1028 => "Dell",
        0x17AA => "Lenovo",
        0x1025 => "Acer",
        _ => $"0x{vendorId:X4}",
    };

    public string SubsystemText => SubsystemId is { } d && SubsystemVendorId is { } v
        ? $"{v:X4}:{d:X4} ({VendorNameOf(v)})" : HardwareProperty_NotAvailable;

    private const string HardwareProperty_NotAvailable = "Not Available";
}

/// <summary>Decodes PCI Express link properties as reported by Windows (DEVPKEY_PciDevice_*LinkSpeed).</summary>
public static class PcieLink
{
    /// <summary>Windows PCI_EXPRESS_LINK_SPEED encoding: 1 = 2.5 GT/s … 6 = 64 GT/s.</summary>
    public static string? SpeedText(uint? code) => code switch
    {
        1 => "2.5 GT/s (PCIe 1.x)",
        2 => "5.0 GT/s (PCIe 2.0)",
        3 => "8.0 GT/s (PCIe 3.0)",
        4 => "16 GT/s (PCIe 4.0)",
        5 => "32 GT/s (PCIe 5.0)",
        6 => "64 GT/s (PCIe 6.0)",
        _ => null,
    };

    public static int? Generation(uint? code) => code is >= 1 and <= 6 ? (int)code : null;

    public static string? WidthText(uint? width) => width is > 0 and <= 32 ? $"x{width}" : null;
}
