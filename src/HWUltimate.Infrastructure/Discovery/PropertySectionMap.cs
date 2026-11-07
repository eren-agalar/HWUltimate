using HWUltimate.Domain.Hardware;

namespace HWUltimate.Infrastructure.Discovery;

/// <summary>Assigns a presentation section to properties whose source did not set one.</summary>
public static class PropertySectionMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Manufacturer"] = PropertySections.Identification,
        ["Model"] = PropertySections.Identification,
        ["Processor ID"] = PropertySections.Identification,
        ["PNP device ID"] = PropertySections.Identification,
        ["Hardware ID"] = PropertySections.Identification,
        ["Instance ID"] = PropertySections.Identification,
        ["Device class"] = PropertySections.Identification,
        ["UUID"] = PropertySections.Identification,
        ["SKU"] = PropertySections.Identification,
        ["Family"] = PropertySections.Identification,
        ["MAC address"] = PropertySections.Identification,
        ["Manufacturer code"] = PropertySections.Identification,
        ["Product code"] = PropertySections.Identification,
        ["Slot"] = PropertySections.Identification,
        ["Bank"] = PropertySections.Identification,
        ["Description"] = PropertySections.Identification,
        ["Cores"] = PropertySections.Specifications,
        ["Threads"] = PropertySections.Specifications,
        ["Logical processors"] = PropertySections.Specifications,
        ["Capacity"] = PropertySections.Specifications,
        ["Type"] = PropertySections.Specifications,
        ["Form factor"] = PropertySections.Specifications,
        ["Dedicated memory"] = PropertySections.Specifications,
        ["L2 cache"] = PropertySections.Specifications,
        ["L3 cache"] = PropertySections.Specifications,
        ["Media type"] = PropertySections.Specifications,
        ["Bus"] = PropertySections.Specifications,
        ["Interface"] = PropertySections.Specifications,
        ["Media"] = PropertySections.Specifications,
        ["Chemistry"] = PropertySections.Specifications,
        ["Spindle speed"] = PropertySections.Specifications,
        ["Rated speed"] = PropertySections.Specifications,
        ["Base clock"] = PropertySections.Specifications,
        ["Max speed"] = PropertySections.Specifications,
        ["External clock"] = PropertySections.Specifications,
        ["Socket"] = PropertySections.Specifications,
        ["Configured speed"] = PropertySections.Performance,
        ["Configured voltage"] = PropertySections.Performance,
        ["Speed at boot"] = PropertySections.Performance,
        ["Current mode"] = PropertySections.Performance,
        ["Link speed"] = PropertySections.Performance,
        ["Virtualization (firmware)"] = PropertySections.Performance,
        ["Driver version"] = PropertySections.Firmware,
        ["Driver date"] = PropertySections.Firmware,
        ["Firmware"] = PropertySections.Firmware,
        ["Revision"] = PropertySections.Firmware,
        ["BIOS vendor"] = PropertySections.Firmware,
        ["BIOS version"] = PropertySections.Firmware,
        ["BIOS date"] = PropertySections.Firmware,
        ["BIOS ROM size"] = PropertySections.Firmware,
        ["SMBIOS version"] = PropertySections.Firmware,
        ["Location"] = PropertySections.Advanced,
        ["Partitions"] = PropertySections.Advanced,
        ["Status"] = PropertySections.Overview,
        ["Health"] = PropertySections.Overview,
        ["Connection"] = PropertySections.Overview,
        ["Enabled"] = PropertySections.Overview,
        ["Charge"] = PropertySections.Overview,
        ["Device ID"] = PropertySections.Advanced,
        ["Manufactured"] = PropertySections.Identification,
    };

    public static HardwareProperty Assign(HardwareProperty p) =>
        p.Group is not null ? p : p with { Group = Map.GetValueOrDefault(p.Name, PropertySections.Advanced) };
}
