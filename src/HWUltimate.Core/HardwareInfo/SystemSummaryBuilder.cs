using System.Globalization;
using HWUltimate.Domain.Hardware;

namespace HWUltimate.Core.HardwareInfo;

/// <summary>
/// The "System summary" page: processor, graphics, motherboard, memory, drives, displays, network and operating system
/// on one screen. Only values that discovery actually produced are used (by property name, in priority order); nothing
/// is derived or estimated, and a device with no matching properties is listed by name alone. Works the same on any
/// vendor's hardware because it relies on HWUltimate's own discovery property names, not on model lists.
/// </summary>
public static class SystemSummaryBuilder
{
    public const string ProcessorSection = "Processor";
    public const string GraphicsSection = "Graphics";
    public const string MotherboardSection = "Motherboard";
    public const string MemorySection = "Memory";
    public const string StorageSection = "Storage";
    public const string DisplaySection = "Displays";
    public const string NetworkSection = "Network";
    public const string SystemSection = "Operating System";

    /// <summary>Summary sections in display order.</summary>
    public static IReadOnlyList<string> SectionOrder { get; } =
        [ProcessorSection, GraphicsSection, MotherboardSection, MemorySection, StorageSection, DisplaySection, NetworkSection, SystemSection];

    private static readonly string[] CpuKeys = ["Cores / threads", "Performance cores (P)", "Efficiency cores (E)", "Codename", "Microarchitecture", "Socket", "Base frequency", "Maximum boost frequency", "L2 cache", "L3 cache", "Manufacturing process", "Instruction sets"];
    private static readonly string[] GpuKeys = ["GPU chip", "VRAM capacity", "VRAM type", "PCIe link (current)", "Driver version"];
    private static readonly string[] BoardKeys = ["Manufacturer", "Chipset", "Chipset (LPC / ISA bridge)", "Super I/O / hardware monitor", "BIOS version", "BIOS date"];
    private static readonly string[] MemoryKeys = ["Total installed", "Memory type", "Current operating speed", "Rated speed", "Modules installed", "Memory slots"];
    private static readonly string[] ModuleKeys = ["Capacity", "Type", "Configured speed", "Rated speed"];
    private static readonly string[] DriveKeys = ["Capacity", "Interface", "Bus", "Media type", "Media"];
    private static readonly string[] DisplayKeys = ["Connection", "Current mode"];
    private static readonly string[] NetworkKeys = ["Link speed", "Negotiated speed", "IPv4 address"];

    public static DeviceInfo Build(HardwareInventory inventory, string title)
    {
        var props = new List<HardwareProperty>();
        var devices = inventory.Devices;
        var s = inventory.Summary;

        // Processor(s): one block of rows per package.
        foreach (var cpu in devices.Where(d => d.Category == HardwareCategory.Cpu))
        {
            props.Add(new HardwareProperty("Name", cpu.Name, ProcessorSection, cpu.Source));
            props.AddRange(Pick(cpu, CpuKeys, ProcessorSection));
        }

        if (!devices.Any(d => d.Category == HardwareCategory.Cpu) && !string.IsNullOrWhiteSpace(s.Processor))
        {
            props.Add(new HardwareProperty("Name", s.Processor!, ProcessorSection, "WMI"));
            if (s.PhysicalCores is { } c) props.Add(new HardwareProperty("Cores / threads", $"{c} / {s.LogicalProcessors}", ProcessorSection, "WMI"));
        }

        // Graphics: one row per adapter, key facts joined.
        foreach (var gpu in devices.Where(d => d.Category == HardwareCategory.Gpu)) props.Add(Line(gpu, GpuKeys, GraphicsSection));

        // Motherboard.
        var board = devices.FirstOrDefault(d => d.Category == HardwareCategory.Motherboard);
        if (board is not null)
        {
            props.Add(new HardwareProperty("Model", board.Name, MotherboardSection, board.Source));
            props.AddRange(Pick(board, BoardKeys, MotherboardSection));
        }
        else if (!string.IsNullOrWhiteSpace(s.Motherboard))
        {
            props.Add(new HardwareProperty("Model", s.Motherboard!, MotherboardSection, "WMI"));
        }

        if (!string.IsNullOrWhiteSpace(s.SystemManufacturer) || !string.IsNullOrWhiteSpace(s.SystemModel))
        {
            props.Add(new HardwareProperty("System", $"{s.SystemManufacturer} {s.SystemModel}".Trim(), MotherboardSection, "WMI"));
        }

        // Memory: totals, then one row per module.
        var memory = devices.FirstOrDefault(d => d.Category == HardwareCategory.Memory);
        if (memory is not null) props.AddRange(Pick(memory, MemoryKeys, MemorySection));
        else if (s.TotalMemoryBytes is { } bytes) props.Add(new HardwareProperty("Total installed", $"{bytes / 1024d / 1024 / 1024:0.#} GB", MemorySection, "WMI"));
        foreach (var module in devices.Where(d => d.Category == HardwareCategory.MemoryModule)) props.Add(Line(module, ModuleKeys, MemorySection));

        foreach (var drive in devices.Where(d => d.Category == HardwareCategory.Storage)) props.Add(Line(drive, DriveKeys, StorageSection));
        foreach (var display in devices.Where(d => d.Category == HardwareCategory.Display)) props.Add(Line(display, DisplayKeys, DisplaySection));

        // Network: connected adapters only (virtual and disconnected ones are in the device tree).
        foreach (var nic in devices.Where(d => d.Category == HardwareCategory.Network && IsConnected(d))) props.Add(Line(nic, NetworkKeys, NetworkSection));

        void Os(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) props.Add(new HardwareProperty(name, value!, SystemSection, "WMI"));
        }

        Os("Operating system", s.OperatingSystem);
        Os("Version", s.OsVersion is null ? null : string.IsNullOrEmpty(s.OsBuild) ? s.OsVersion : $"{s.OsVersion} (build {s.OsBuild})");
        Os("Computer name", s.ComputerName);
        Os("Uptime", s.Uptime is { } u ? string.Create(CultureInfo.InvariantCulture, $"{(int)u.TotalDays}d {u.Hours}h {u.Minutes}m") : null);

        return new DeviceInfo { Id = "summary", Name = title, Category = HardwareCategory.System, Source = "HWUltimate", Properties = props };
    }

    private static IEnumerable<HardwareProperty> Pick(DeviceInfo device, string[] names, string section)
    {
        foreach (var name in names)
        {
            var p = device.Properties.FirstOrDefault(x => x.IsAvailable && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (p is not null) yield return p with { Group = section };
        }
    }

    /// <summary>One row for a device: its name and the first available key facts, e.g. "16 GB · DDR5 · 6000 MT/s".</summary>
    private static HardwareProperty Line(DeviceInfo device, string[] names, string section)
    {
        var facts = Pick(device, names, section).Select(p => p.Value.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        return new HardwareProperty(device.Name, facts.Count > 0 ? string.Join("  ·  ", facts) : device.Category.ToString(), section, device.Source);
    }

    private static bool IsConnected(DeviceInfo nic)
    {
        var status = nic.Properties.FirstOrDefault(p => p.IsAvailable && p.Name is "Operational status" or "Status" or "Interface status")?.Value;
        return status is null
               || status.Equals("Up", StringComparison.OrdinalIgnoreCase)
               || (status.Contains("connected", StringComparison.OrdinalIgnoreCase) && !status.Contains("disconnected", StringComparison.OrdinalIgnoreCase));
    }
}
