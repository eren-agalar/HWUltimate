using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HWUltimate.Core.HardwareInfo;
using HWUltimate.Domain.Hardware;
using HWUltimate.Hardware.Nvidia;
using HWUltimate.Infrastructure.Cpu;
using HWUltimate.Infrastructure.Discovery;
using HWUltimate.Infrastructure.Smbios;

namespace HWUltimate.Tests;

/// <summary>Synthetic CPUID dumps modelled on real processors.</summary>
internal static class CpuIdFixtures
{
    private static uint[] Pack(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s.PadRight(48, '\0'));
        return Enumerable.Range(0, 12).Select(i => BitConverter.ToUInt32(bytes, i * 4)).ToArray();
    }

    private static uint Reg(string four) => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(four));

    private static CpuIdRegisters Cache(int type, int level, int ways, int line, int sets, int sharing) =>
        new((uint)(type | (level << 5) | ((sharing - 1) << 14)), (uint)((line - 1) | ((ways - 1) << 22)), (uint)(sets - 1), 0);

    /// <summary>AMD Zen 4 desktop part (signature 0x00A60F12 = family 0x19, model 0x61).</summary>
    public static CpuIdFunction Zen4()
    {
        var b = Pack("AMD Ryzen 9 7950X 16-Core Processor            ");
        var map = new Dictionary<(uint, uint), CpuIdRegisters>
        {
            [(0, 0)] = new(0x10, Reg("Auth"), Reg("cAMD"), Reg("enti")),
            [(1, 0)] = new(0x00A60F12, 0, (1u << 0) | (1u << 9) | (1u << 19) | (1u << 20) | (1u << 25) | (1u << 28), (1u << 25) | (1u << 26)),
            [(7, 0)] = new(0, (1u << 5) | (1u << 16) | (1u << 29), 0, 0),
            [(0x80000000, 0)] = new(0x80000028, 0, 0, 0),
            [(0x80000001, 0)] = new(0, 0, 1u << 2, 1u << 29),
            [(0x80000002, 0)] = new(b[0], b[1], b[2], b[3]),
            [(0x80000003, 0)] = new(b[4], b[5], b[6], b[7]),
            [(0x80000004, 0)] = new(b[8], b[9], b[10], b[11]),
            [(0x8000001D, 0)] = Cache(1, 1, 8, 64, 64, 2),
            [(0x8000001D, 1)] = Cache(2, 1, 8, 64, 64, 2),
            [(0x8000001D, 2)] = Cache(3, 2, 8, 64, 2048, 2),
            [(0x8000001D, 3)] = Cache(3, 3, 16, 64, 32768, 16),
        };
        return (leaf, sub) => map.GetValueOrDefault((leaf, sub));
    }

    /// <summary>Intel hybrid part (family 6, model 0xB7) with leaf 0x16 frequencies.</summary>
    public static CpuIdFunction RaptorLake()
    {
        var map = new Dictionary<(uint, uint), CpuIdRegisters>
        {
            [(0, 0)] = new(0x20, Reg("Genu"), Reg("ntel"), Reg("ineI")),
            [(1, 0)] = new(0x000B0671, 0, 1u << 5, 1u << 26),
            [(7, 0)] = new(1, 1u << 5, 0, 1u << 15),
            [(7, 1)] = new(1u << 4, 0, 0, 0),
            [(0x16, 0)] = new(3000, 5800, 100, 0),
            [(4, 0)] = Cache(1, 1, 12, 64, 64, 2),
            [(0x80000000, 0)] = new(0x80000001, 0, 0, 0),
        };
        return (leaf, sub) => map.GetValueOrDefault((leaf, sub));
    }
}

public class CpuIdDecoderTests
{
    [Fact]
    public void Decodes_amd_identity_features_and_caches()
    {
        var id = CpuIdDecoder.Decode(CpuIdFixtures.Zen4());
        Assert.Equal("AuthenticAMD", id.Vendor);
        Assert.Equal("AMD Ryzen 9 7950X 16-Core Processor", id.Brand);
        Assert.Equal((0x19, 0x61, 2), (id.Family, id.Model, id.Stepping));
        Assert.Equal(("Zen 4", "Raphael"), (id.Microarchitecture, id.Codename));
        Assert.True(id.Has("AVX2") && id.Has("AVX-512F") && id.Has("SHA") && id.Has("AES-NI") && id.Has("SSE4.2"));
        Assert.False(id.Has("AVX-512FP16"));
        Assert.Equal("AMD-V (SVM)", id.VirtualizationTechnology);
        Assert.True(id.Is64Bit);
        Assert.Equal(4, id.Caches.Count);
        Assert.Equal(32 * 1024, id.Caches[0].SizeBytes);
        Assert.Equal(CacheType.Instruction, id.Caches[1].Type);
        Assert.Equal(1024 * 1024, id.Caches[2].SizeBytes);
        Assert.Equal(32L * 1024 * 1024, id.Caches[3].SizeBytes);
        Assert.Null(id.MaxFrequencyMhz); // AMD does not implement leaf 0x16
    }

    [Fact]
    public void Decodes_intel_hybrid_and_frequencies()
    {
        var id = CpuIdDecoder.Decode(CpuIdFixtures.RaptorLake());
        Assert.Equal((6, 0xB7), (id.Family, id.Model));
        Assert.Equal("Raptor Lake", id.Codename);
        Assert.True(id.IsHybrid);
        Assert.True(id.Has("AVX-VNNI"));
        Assert.Equal("Intel VT-x", id.VirtualizationTechnology);
        Assert.Equal((3000, 5800, 100), (id.BaseFrequencyMhz!.Value, id.MaxFrequencyMhz!.Value, id.BusFrequencyMhz!.Value));
        Assert.Null(id.Brand); // max extended leaf too low for the brand string
    }

    [Fact]
    public void Unknown_model_is_not_guessed()
    {
        Assert.Equal((null, null), CpuMicroarchitecture.Lookup("GenuineIntel", 6, 0x01));
        Assert.Equal((null, null), CpuMicroarchitecture.Lookup("CentaurHauls", 6, 0x0F));
    }

    [Fact]
    public void Cpu_device_reports_unsupported_properties_explicitly()
    {
        var id = CpuIdDecoder.Decode(CpuIdFixtures.Zen4());
        var device = CpuDiscoveryService.Build(id, null, microcode: null, nominalMhz: 4500, wmi: null);
        var tdp = device.Properties.Single(p => p.Name == "TDP");
        Assert.Equal(PropertyStatus.NotAvailable, tdp.Status);
        Assert.Equal(HardwareProperty.NotAvailableText, tdp.Value);
        Assert.Equal(PropertyStatus.NotAvailable, device.Properties.Single(p => p.Name == "Microcode revision").Status);
        Assert.Equal(PropertyStatus.NotAvailable, device.Properties.Single(p => p.Name == "Maximum boost frequency").Status);
        Assert.Equal("Raphael", device.Properties.Single(p => p.Name == "Codename").Value);
        Assert.All(device.Properties, p => Assert.Contains(p.Group, PropertySections.All));
        Assert.All(device.Properties.Where(p => p.IsAvailable), p => Assert.False(string.IsNullOrEmpty(p.Source)));
    }
}

public class ProcessorTopologyTests
{
    private static byte[] Entry(int relationship, int size, Action<byte[]> fill)
    {
        var b = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(b, relationship);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), size);
        fill(b);
        return b;
    }

    private static byte[] Core(byte effClass, bool smt) => Entry(0, 48, b =>
    {
        b[8] = (byte)(smt ? 1 : 0);
        b[9] = effClass;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), smt ? 0b11UL : 0b1UL);
    });

    private static byte[] CacheEntry(byte level, int type, uint size) => Entry(2, 56, b =>
    {
        b[8] = level;
        b[9] = 12;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), size);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), type);
    });

    [Fact]
    public void Parses_hybrid_topology_and_cache_instances()
    {
        var parts = new List<byte[]> { Entry(3, 48, _ => { }) };
        parts.AddRange(Enumerable.Range(0, 2).Select(_ => Core(1, true)));
        parts.AddRange(Enumerable.Range(0, 4).Select(_ => Core(0, false)));
        parts.AddRange(Enumerable.Range(0, 6).Select(_ => CacheEntry(1, 2, 48 * 1024)));
        parts.Add(CacheEntry(3, 0, 24u * 1024 * 1024));
        var topo = ProcessorTopology.Parse(parts.SelectMany(p => p).ToArray());

        Assert.Equal(1, topo.Packages);
        Assert.Equal(6, topo.Cores);
        Assert.Equal(8, topo.LogicalProcessors);
        Assert.True(topo.Smt);
        Assert.Equal((2, 4), topo.HybridSplit);
        var l1d = topo.Caches.Single(c => c.Level == 1);
        Assert.Equal(("Data", 48L * 1024, 6), (l1d.Type, l1d.SizeBytes, l1d.Instances));
        Assert.Equal(1, topo.Caches.Single(c => c.Level == 3).Instances);
    }

    [Fact]
    public void Non_hybrid_has_no_split_and_truncated_buffer_is_safe()
    {
        var buf = Core(0, true).Concat(Core(0, true)).ToArray();
        Assert.Null(ProcessorTopology.Parse(buf).HybridSplit);
        Assert.Equal(1, ProcessorTopology.Parse(buf.AsSpan(0, 60)).Cores);
    }
}

public class PciIdentityTests
{
    [Fact]
    public void Parses_full_pnp_id()
    {
        var id = PciIdentity.Parse(@"PCI\VEN_10DE&DEV_2684&SUBSYS_16F110DE&REV_A1\4&2283F625&0&0019")!;
        Assert.Equal((0x10DE, 0x2684), ((int)id.VendorId, (int)id.DeviceId));
        Assert.Equal((ushort)0x10DE, id.SubsystemVendorId!.Value);
        Assert.Equal((ushort)0x16F1, id.SubsystemId!.Value);
        Assert.Equal((byte)0xA1, id.Revision!.Value);
        Assert.Equal("NVIDIA", id.VendorName);
        Assert.Equal("10DE:16F1 (NVIDIA)", id.SubsystemText);
    }

    [Fact]
    public void Handles_missing_parts()
    {
        var id = PciIdentity.Parse(@"PCI\VEN_1002&DEV_744C")!;
        Assert.Null(id.SubsystemId);
        Assert.Null(id.Revision);
        Assert.Equal("Not Available", id.SubsystemText);
        Assert.Null(PciIdentity.Parse(@"ROOT\BasicDisplay\0000"));
        Assert.Null(PciIdentity.Parse(null));
        Assert.Equal("0xABCD", PciIdentity.VendorNameOf(0xABCD));
    }

    [Theory]
    [InlineData(4u, "16 GT/s (PCIe 4.0)", 4)]
    [InlineData(5u, "32 GT/s (PCIe 5.0)", 5)]
    [InlineData(0u, null, null)]
    [InlineData(9u, null, null)]
    public void Decodes_pcie_link_speed(uint code, string? text, int? gen)
    {
        Assert.Equal(text, PcieLink.SpeedText(code));
        Assert.Equal(gen, PcieLink.Generation(code));
    }

    [Fact]
    public void Chip_catalog_lookup_and_unknown()
    {
        var ad102 = GpuChipCatalog.Find(0x10DE, 0x2684)!;
        Assert.Equal(("AD102", "Ada Lovelace", "TSMC 4N"), (ad102.Chip, ad102.Architecture, ad102.Process));
        Assert.Null(GpuChipCatalog.Find(0x10DE, 0x0001));
        Assert.True(GpuChipCatalog.Count >= 20);
    }
}

public class NvmlDecodeTests
{
    [Fact]
    public void Decodes_clock_event_reasons()
    {
        Assert.Equal(["Software Power Cap", "Hardware Thermal Slowdown"], NvmlDecode.ActiveReasons(0x4 | 0x40));
        Assert.Empty(NvmlDecode.ActiveReasons(0));
        Assert.Equal(9, NvmlDecode.ClockEventReasons.Count);
    }

    [Fact]
    public void Decodes_architecture_and_versions()
    {
        Assert.Equal("Ada Lovelace", NvmlDecode.Architecture(8));
        Assert.Null(NvmlDecode.Architecture(99));
        Assert.Null(NvmlDecode.Architecture(null));
        Assert.Equal("12.4", NvmlDecode.CudaVersion(12040));
        Assert.Equal("WDDM", NvmlDecode.DriverModel(0));
    }
}

public class SmbiosMemoryTests
{
    private static byte[] Structure(byte type, int length, Action<byte[]> fill, params string[] strings)
    {
        var f = new byte[length];
        f[0] = type;
        f[1] = (byte)length;
        fill(f);
        var tail = strings.Length == 0 ? new byte[] { 0, 0 } : Encoding.ASCII.GetBytes(string.Concat(strings.Select(s => s + "\0")) + "\0");
        return [.. f, .. tail];
    }

    private static SmbiosData Parse(params byte[][] structures)
    {
        var table = structures.SelectMany(s => s).Concat(Structure(127, 4, _ => { })).ToArray();
        return SmbiosParser.ParseTable(table, new Version(3, 5));
    }

    private static byte[] Dimm(ushort total, ushort data, string slot) => Structure(17, 0x28, f =>
    {
        BitConverter.GetBytes(total).CopyTo(f, 0x08);
        BitConverter.GetBytes(data).CopyTo(f, 0x0A);
        BitConverter.GetBytes((ushort)16384).CopyTo(f, 0x0C);
        f[0x0E] = 0x09; f[0x10] = 1; f[0x12] = 0x22; f[0x1B] = 2;
        BitConverter.GetBytes((ushort)5600).CopyTo(f, 0x15);
        BitConverter.GetBytes((ushort)4800).CopyTo(f, 0x20);
    }, slot);

    private static byte[] Array16(byte ecc, ushort slots) => Structure(16, 0x17, f =>
    {
        f[0x05] = 3; f[0x06] = ecc;
        BitConverter.GetBytes(128u * 1024 * 1024).CopyTo(f, 0x07); // 128 GB in KB
        BitConverter.GetBytes(slots).CopyTo(f, 0x0D);
    });

    [Fact]
    public void Parses_ecc_array_and_module_widths()
    {
        var d = Parse(Array16(6, 4), Dimm(72, 64, "DIMM_A1"), Dimm(72, 64, "DIMM_B1"));
        var array = Assert.Single(d.Arrays);
        Assert.Equal(("Multi-bit ECC", 4), (array.ErrorCorrection, array.Slots!.Value));
        Assert.Equal(128L * 1024 * 1024 * 1024, array.MaxCapacityBytes);
        Assert.All(d.MemoryDevices, m => Assert.True(m.HasEccBits));
        Assert.Equal(2, d.MemoryDevices[0].Rank);
    }

    [Fact]
    public void Memory_summary_distinguishes_rated_and_configured_and_marks_unknowns()
    {
        var d = Parse(Array16(3, 4), Dimm(64, 64, "DIMM_A1"), Dimm(64, 64, "DIMM_B1"));
        var summary = SmbiosDiscoveryService.MemorySummary(d);
        string V(string name) => summary.Properties.Single(p => p.Name == name).Value;
        Assert.Equal("32 GB", V("Total installed"));
        Assert.Equal("2", V("Modules installed"));
        Assert.Equal("4", V("Memory slots"));
        Assert.Equal("DDR5", V("Memory type"));
        Assert.Equal("5600 MT/s", V("Rated speed"));
        Assert.Equal("4800 MT/s", V("Current operating speed"));
        Assert.Equal("None", V("ECC capability (memory array)"));
        Assert.Equal("Non-ECC modules", V("ECC modules"));
        Assert.Equal(PropertyStatus.NotAvailable, summary.Properties.Single(p => p.Name == "Memory channels").Status);
        Assert.Equal(PropertyStatus.NotAvailable, summary.Properties.Single(p => p.Name == "ECC operating status").Status);
    }

    [Fact]
    public void Module_devices_state_that_spd_and_timings_are_not_available()
    {
        var devices = SmbiosDiscoveryService.ToDevices(Parse(Dimm(64, 64, "DIMM_A1")));
        var module = devices.First(d => d.Id.StartsWith("smbios:mem", StringComparison.Ordinal) && d.Id != "smbios:memory");
        var spd = module.Properties.Single(p => p.Name == "SPD contents");
        Assert.Equal(PropertyStatus.NotAvailable, spd.Status);
        Assert.Contains("not the SPD", spd.Note, StringComparison.Ordinal);
        Assert.Equal(PropertyStatus.NotAvailable, module.Properties.Single(p => p.Name.StartsWith("Timings", StringComparison.Ordinal)).Status);
        Assert.Contains(devices, d => d.Id == "smbios:memory");
    }
}

public class MotherboardAndMergeTests
{
    [Fact]
    public void Motherboard_reports_chipset_and_superio_or_marks_them_unavailable()
    {
        var withData = MotherboardDiscoveryService.Build("ASUS ROG X670E",
            [("AMD Promontory 21 LPC Bridge", @"PCI\VEN_1022&DEV_790E&SUBSYS_88771043&REV_51\3&1")], [("AMD Root Complex", @"PCI\VEN_1022&DEV_14D8")],
            [new HardwareDevice("sio", "Nuvoton NCT6799D", HardwareCategory.SuperIo, "lhm")], new Version(3, 5));
        Assert.Equal("AMD Promontory 21 LPC Bridge", withData.Properties.Single(p => p.Name.StartsWith("Chipset (", StringComparison.Ordinal)).Value);
        Assert.Equal("1022:790E (AMD)", withData.Properties.Single(p => p.Name == "Chipset PCI ID").Value);
        Assert.Equal("Nuvoton NCT6799D", withData.Properties.Single(p => p.Name.StartsWith("Super I/O", StringComparison.Ordinal)).Value);
        Assert.Equal(PropertyStatus.NotAvailable, withData.Properties.Single(p => p.Name == "Embedded controller").Status);

        var empty = MotherboardDiscoveryService.Build("Board", [], [], [], new Version(2, 8));
        Assert.Equal(PropertyStatus.NotAvailable, empty.Properties.Single(p => p.Name == "Chipset").Status);
        Assert.Equal(PropertyStatus.NotAvailable, empty.Properties.Single(p => p.Name.StartsWith("Super I/O", StringComparison.Ordinal)).Status);
    }

    [Fact]
    public void Merge_assigns_sections_to_unsectioned_properties()
    {
        var merged = HardwareInventoryService.Merge(
        [
            new DeviceInfo { Id = "a", Name = "GPU", Category = HardwareCategory.Gpu, Source = "PCI", Properties = [new("VBIOS version", "95.02", PropertySections.Firmware, "Registry")] },
            new DeviceInfo { Id = "b", Name = "GPU", Category = HardwareCategory.Gpu, Source = "WMI", Properties = [new("Driver version", "32.0"), new("Weird", "x")] },
        ]);
        var gpu = Assert.Single(merged);
        Assert.Equal(PropertySections.Firmware, gpu.Properties.Single(p => p.Name == "Driver version").Group);
        Assert.Equal(PropertySections.Advanced, gpu.Properties.Single(p => p.Name == "Weird").Group);
        Assert.Equal(PropertySections.Firmware, gpu.Properties.Single(p => p.Name == "VBIOS version").Group);
    }

    [Fact]
    public void Monitor_output_technology_names()
    {
        Assert.Equal("HDMI", WmiDeviceDiscoveryService.OutputTechnology(5));
        Assert.Equal("DisplayPort", WmiDeviceDiscoveryService.OutputTechnology(10));
        Assert.Equal("Internal", WmiDeviceDiscoveryService.OutputTechnology(0x80000000));
        Assert.Null(WmiDeviceDiscoveryService.OutputTechnology(null));
    }
}

public class HardwareInfoExporterTests
{
    private static HardwareInventory Inventory() => new(DateTimeOffset.Now, new SystemSummary(),
    [
        new DeviceInfo
        {
            Id = "gpu", Name = "RTX <4090>", Category = HardwareCategory.Gpu, Source = "PCI, NVML",
            Properties =
            [
                new("VBIOS version", "95.02.18.80.5F", PropertySections.Firmware, "NVML"),
                new("PCI device ID", "0x2684", PropertySections.Identification, "PnP device id"),
                HardwareProperty.Unavailable("ASIC / silicon quality", PropertySections.Advanced, "No public interface exposes it.", "NVAPI, NVML"),
                new("GPU", "RTX 4090", null, "WMI"),
            ],
        },
        new DeviceInfo { Id = "cpu", Name = "Ryzen", Category = HardwareCategory.Cpu, Source = "CPUID", Properties = [new("Codename", "Raphael", PropertySections.Identification, "CPUID")] },
    ], []);

    [Fact]
    public void Sections_follow_canonical_order_and_default_to_overview()
    {
        var sections = HardwareInfoExporter.Sections(Inventory().Devices[0]).Select(s => s.Section).ToList();
        Assert.Equal([PropertySections.Overview, PropertySections.Identification, PropertySections.Firmware, PropertySections.Advanced], sections);
    }

    [Fact]
    public void Device_report_lists_unavailable_properties_with_reason()
    {
        var text = HardwareInfoExporter.FormatDevice(Inventory().Devices[0]);
        Assert.Contains("[Firmware & Drivers]", text, StringComparison.Ordinal);
        Assert.Contains("ASIC / silicon quality", text, StringComparison.Ordinal);
        Assert.Contains("Not Available", text, StringComparison.Ordinal);
        Assert.Contains("↳ No public interface exposes it.", text, StringComparison.Ordinal);
        Assert.Contains("(NVML)", text, StringComparison.Ordinal);
        Assert.Equal("VBIOS version: 95.02.18.80.5F", HardwareInfoExporter.FormatProperty(Inventory().Devices[0].Properties[0]));
    }

    [Fact]
    public void Exports_json_html_and_text()
    {
        using var json = JsonDocument.Parse(HardwareInfoExporter.Export(Inventory(), HardwareInfoFormat.Json, "1.0"));
        var props = json.RootElement.GetProperty("devices")[0].GetProperty("Properties");
        Assert.Equal("NotAvailable", props[2].GetProperty("Status").GetString());
        Assert.Equal("NVML", props[0].GetProperty("Source").GetString());

        var html = HardwareInfoExporter.Export(Inventory(), HardwareInfoFormat.Html, "1.0");
        Assert.Contains("RTX &lt;4090&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<4090>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"na\"", html, StringComparison.Ordinal);

        var text = HardwareInfoExporter.Export(Inventory(), HardwareInfoFormat.Text, "1.0");
        Assert.Contains("HWULTIMATE — HARDWARE INFORMATION", text, StringComparison.Ordinal);
        Assert.Contains("Raphael", text, StringComparison.Ordinal);
        Assert.Equal(".html", HardwareInfoExporter.FileExtension(HardwareInfoFormat.Html));
    }

    [Fact]
    public void Search_matches_names_values_sources_and_devices()
    {
        var inv = Inventory();
        Assert.Single(HardwareInfoExporter.Search(inv, "vbios"));
        Assert.Single(HardwareInfoExporter.Search(inv, "0x2684"));
        Assert.Equal(2, HardwareInfoExporter.Search(inv, "nvml").Count); // VBIOS source + ASIC checked-sources
        Assert.Equal(4, HardwareInfoExporter.Search(inv, "rtx <4090>").Count); // device-name hit returns all its properties
        Assert.Empty(HardwareInfoExporter.Search(inv, "   "));
        Assert.Empty(HardwareInfoExporter.Search(inv, "does-not-exist"));
    }
}

public class SystemSummaryTests
{
    private static DeviceInfo D(string id, string name, HardwareCategory c, params (string Name, string Value)[] props) => new()
    {
        Id = id, Name = name, Category = c, Source = "test",
        Properties = props.Select(p => new HardwareProperty(p.Name, p.Value, PropertySections.Overview, "test")).ToList(),
    };

    [Fact]
    public void Summary_lists_every_component_from_discovered_values_only()
    {
        var inv = new HardwareInventory(DateTimeOffset.Now, new SystemSummary { OperatingSystem = "Windows 11 Pro", OsVersion = "10.0.26100", OsBuild = "26100", ComputerName = "PC" },
        [
            D("c", "AMD Ryzen 7 7800X3D", HardwareCategory.Cpu, ("Cores / threads", "8 / 16"), ("Codename", "Raphael"), ("Unrelated", "x")),
            D("g1", "NVIDIA GeForce RTX 4070", HardwareCategory.Gpu, ("VRAM capacity", "12 GB"), ("Driver version", "566.36")),
            D("g2", "AMD Radeon(TM) Graphics", HardwareCategory.Gpu),
            D("b", "ASUS TUF GAMING B650-PLUS", HardwareCategory.Motherboard, ("Chipset", "AMD B650"), ("BIOS version", "3024")),
            D("m", "System Memory", HardwareCategory.Memory, ("Total installed", "32 GB"), ("Memory type", "DDR5")),
            D("m1", "Kingston KF560C36-16 (DIMM_A2)", HardwareCategory.MemoryModule, ("Capacity", "16 GB"), ("Type", "DDR5"), ("Configured speed", "6000 MT/s")),
            D("s", "Samsung SSD 990 PRO 2TB", HardwareCategory.Storage, ("Capacity", "2000 GB"), ("Bus", "NVMe")),
            D("n1", "Intel Ethernet I226-V", HardwareCategory.Network, ("Interface status", "Up"), ("Link speed", "2.5 Gbps")),
            D("n2", "Hyper-V Virtual Ethernet Adapter", HardwareCategory.Network, ("Interface status", "Down")),
        ], []);
        var summary = SystemSummaryBuilder.Build(inv, "System summary");
        var sections = HardwareInfoExporter.Sections(summary).Select(s => s.Section).ToList();
        Assert.Equal([SystemSummaryBuilder.ProcessorSection, SystemSummaryBuilder.GraphicsSection, SystemSummaryBuilder.MotherboardSection, SystemSummaryBuilder.MemorySection,
            SystemSummaryBuilder.StorageSection, SystemSummaryBuilder.NetworkSection, SystemSummaryBuilder.SystemSection], sections);

        var p = summary.Properties;
        Assert.Contains(p, x => x.Name == "Cores / threads" && x.Value == "8 / 16");
        Assert.DoesNotContain(p, x => x.Name == "Unrelated");
        Assert.Contains(p, x => x.Name == "NVIDIA GeForce RTX 4070" && x.Value == "12 GB  ·  566.36");
        Assert.Contains(p, x => x.Name == "AMD Radeon(TM) Graphics"); // iGPU listed even without extra facts
        Assert.Contains(p, x => x.Name == "Kingston KF560C36-16 (DIMM_A2)" && x.Value == "16 GB  ·  DDR5  ·  6000 MT/s");
        Assert.Contains(p, x => x.Name == "Intel Ethernet I226-V");
        Assert.DoesNotContain(p, x => x.Name.StartsWith("Hyper-V", StringComparison.Ordinal));
        Assert.Contains(p, x => x.Name == "Version" && x.Value == "10.0.26100 (build 26100)");
    }

    [Fact]
    public void Empty_inventory_still_gives_a_summary_from_wmi()
    {
        var summary = SystemSummaryBuilder.Build(new HardwareInventory(DateTimeOffset.Now, new SystemSummary { Processor = "Intel Core i5-8250U", PhysicalCores = 4, LogicalProcessors = 8 }, [], []), "S");
        Assert.Contains(summary.Properties, x => x.Name == "Name" && x.Value == "Intel Core i5-8250U");
        Assert.Contains(summary.Properties, x => x.Name == "Cores / threads" && x.Value == "4 / 8");
    }
}
