using Microsoft.Extensions.Logging.Abstractions;
using HWUltimate.Core.Identity;
using HWUltimate.Core.Sensors;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;
using HWUltimate.Infrastructure.Discovery;
using HWUltimate.Infrastructure.Gpu;

namespace HWUltimate.Tests;

public class PnpPathTests
{
    [Fact]
    public void Normalizes_interface_paths_and_instance_ids()
    {
        const string iface = @"\\?\pci#ven_10de&dev_2206&subsys_38971462&rev_a1#4&2283f625&0&0019#{1ca05180-a699-450a-9a0c-de4fbe3ddd89}";
        Assert.Equal(@"PCI\VEN_10DE&DEV_2206&SUBSYS_38971462&REV_A1\4&2283F625&0&0019", PnpPath.Normalize(iface));
        Assert.Equal(@"PCI\VEN_10DE&DEV_2206&SUBSYS_38971462&REV_A1\4&2283F625&0&0019", PnpPath.Normalize(@"pci\ven_10de&dev_2206&subsys_38971462&rev_a1\4&2283f625&0&0019"));
        Assert.Null(PnpPath.Normalize(@"ROOT\BasicDisplay\0000"));
        Assert.Null(PnpPath.Normalize(""));
        var id = PnpPath.FromPath(iface);
        Assert.Equal((ushort)0x10DE, id.VendorId);
        Assert.Equal((ushort)0x2206, id.DeviceId);
        Assert.Equal((ushort)0x1462, id.SubsystemVendorId);
        Assert.Equal("10DE:2206 / 1462:3897", id.PciIdText);
    }
}

public class SensorKindClassifierTests
{
    [Theory]
    [InlineData("Temperature Sensor Resolution", SensorType.Temperature)]
    [InlineData("Thermal Sensor Low Limit", SensorType.Temperature)]
    [InlineData("Thermal Sensor High Limit", SensorType.Temperature)]
    [InlineData("Thermal Sensor Critical Limit", SensorType.Temperature)]
    [InlineData("Warning Composite Temperature", SensorType.Temperature)]
    [InlineData("Critical Composite Temperature", SensorType.Temperature)]
    [InlineData("GPU Slowdown Temperature Threshold", SensorType.Temperature)]
    [InlineData("TjMax", SensorType.Temperature)]
    [InlineData("Temperature Target", SensorType.Temperature)]
    [InlineData("Sensor Resolution", SensorType.Voltage)]
    public void Detects_metadata(string name, SensorType type) => Assert.Equal(SensorKind.Metadata, SensorKindClassifier.Classify(type, name));

    [Theory]
    [InlineData("CPU Package", SensorType.Temperature)]
    [InlineData("Core #1 Distance to TjMax", SensorType.Temperature)]
    [InlineData("Composite Temperature", SensorType.Temperature)]
    [InlineData("GPU Hot Spot", SensorType.Temperature)]
    [InlineData("Power Limit (enforced)", SensorType.Power)]
    [InlineData("Vcore", SensorType.Voltage)]
    [InlineData("Core #3", SensorType.Clock)]
    [InlineData("CPU Total", SensorType.Utilization)]
    public void Keeps_real_measurements(string name, SensorType type) => Assert.Equal(SensorKind.Measurement, SensorKindClassifier.Classify(type, name));
}

public class PhysicalDeviceMergerTests
{
    private const string NvPath = @"PCI\VEN_10DE&DEV_2206&SUBSYS_38971462&REV_A1\4&2283F625&0&0019";
    private const string AmdPath = @"PCI\VEN_1002&DEV_164E&SUBSYS_88771043&REV_C1\4&1A2B3C4D&0&0041";
    private static readonly PciLocation NvBus = new(0, 1, 0, 0);
    private static readonly PciLocation AmdBus = new(0, 0x0C, 0, 0);

    private static int Priority(string p) => p switch { "lhm" => 0, "nvapi" => 10, _ => 20 };

    private sealed class MapResolver(Dictionary<string, PciLocation> map) : IDeviceIdentityResolver
    {
        public DeviceIdentity Resolve(DeviceIdentity id) =>
            id.InstancePath is { } p && id.Location is null && map.TryGetValue(p, out var l) ? id with { Location = l } : id;
    }

    private static HardwareDevice Dev(string id, string name, string provider, DeviceIdentity? identity, HardwareCategory c = HardwareCategory.Gpu) =>
        new(id, name, c, provider, Identity: identity);

    [Fact]
    public void Same_gpu_from_three_providers_becomes_one_device()
    {
        var devices = new[]
        {
            Dev("lhm:/gpu-nvidia/0", "NVIDIA GeForce RTX 3080", "lhm", PnpPath.FromPath(NvPath)),
            Dev("nvapi:gpu0", "NVIDIA GeForce RTX 3080", "nvapi", new DeviceIdentity { Location = NvBus, VendorId = 0x10DE, DeviceId = 0x2206 }),
        };
        var result = PhysicalDeviceMerger.Merge(devices, Priority, new MapResolver(new() { [NvPath] = NvBus }));
        var gpu = Assert.Single(result.Devices);
        Assert.Equal("pci:0000:01:00.0", gpu.DeviceId);
        Assert.Equal(["lhm", "nvapi"], gpu.Providers);
        Assert.Equal(NvPath, gpu.Identity!.InstancePath);
        Assert.Equal(gpu.DeviceId, result.PhysicalIdOf["nvapi:gpu0"]);
        Assert.Equal(gpu.DeviceId, result.PhysicalIdOf["lhm:/gpu-nvidia/0"]);
    }

    [Fact]
    public void Integrated_and_discrete_gpus_stay_separate()
    {
        var devices = new[]
        {
            Dev("lhm:/gpu-amd/0", "AMD Radeon(TM) Graphics", "lhm", PnpPath.FromPath(AmdPath)),
            Dev("lhm:/gpu-nvidia/0", "NVIDIA GeForce RTX 3080", "lhm", PnpPath.FromPath(NvPath)),
            Dev("nvapi:gpu0", "NVIDIA GeForce RTX 3080", "nvapi", new DeviceIdentity { Location = NvBus, VendorId = 0x10DE, DeviceId = 0x2206 }),
        };
        var result = PhysicalDeviceMerger.Merge(devices, Priority, new MapResolver(new() { [NvPath] = NvBus, [AmdPath] = AmdBus }));
        Assert.Equal(2, result.Devices.Count);
        Assert.Contains(result.Devices, d => d.Name.StartsWith("AMD", StringComparison.Ordinal) && d.Providers.SequenceEqual(["lhm"]));
        Assert.Contains(result.Devices, d => d.Name.StartsWith("NVIDIA", StringComparison.Ordinal) && d.Providers.Count == 2);
    }

    [Fact]
    public void Two_identical_physical_gpus_are_never_merged()
    {
        var b1 = new PciLocation(0, 1, 0, 0);
        var b2 = new PciLocation(0, 2, 0, 0);
        var devices = new[]
        {
            Dev("lhm:/gpu-nvidia/0", "NVIDIA GeForce RTX 3080", "lhm", new DeviceIdentity { Location = b1, VendorId = 0x10DE, DeviceId = 0x2206 }),
            Dev("lhm:/gpu-nvidia/1", "NVIDIA GeForce RTX 3080", "lhm", new DeviceIdentity { Location = b2, VendorId = 0x10DE, DeviceId = 0x2206 }),
            Dev("nvapi:gpu0", "NVIDIA GeForce RTX 3080", "nvapi", new DeviceIdentity { Location = b2, VendorId = 0x10DE, DeviceId = 0x2206 }),
            Dev("nvapi:gpu1", "NVIDIA GeForce RTX 3080", "nvapi", new DeviceIdentity { Location = b1, VendorId = 0x10DE, DeviceId = 0x2206 }),
        };
        var result = PhysicalDeviceMerger.Merge(devices, Priority);
        Assert.Equal(2, result.Devices.Count);
        Assert.Equal(result.PhysicalIdOf["lhm:/gpu-nvidia/0"], result.PhysicalIdOf["nvapi:gpu1"]); // matched by bus, not by order
        Assert.Equal(result.PhysicalIdOf["lhm:/gpu-nvidia/1"], result.PhysicalIdOf["nvapi:gpu0"]);
    }

    [Fact]
    public void Identical_gpus_without_location_are_not_guessed()
    {
        // Both providers see two 10DE:2206 GPUs but only one side knows locations → ambiguous → no merge.
        var devices = new[]
        {
            Dev("lhm:a", "RTX 3080", "lhm", new DeviceIdentity { VendorId = 0x10DE, DeviceId = 0x2206 }),
            Dev("lhm:b", "RTX 3080", "lhm", new DeviceIdentity { VendorId = 0x10DE, DeviceId = 0x2206 }),
            Dev("nv:a", "RTX 3080", "nvapi", new DeviceIdentity { Location = new(0, 1, 0, 0), VendorId = 0x10DE, DeviceId = 0x2206 }),
            Dev("nv:b", "RTX 3080", "nvapi", new DeviceIdentity { Location = new(0, 2, 0, 0), VendorId = 0x10DE, DeviceId = 0x2206 }),
        };
        Assert.Equal(4, PhysicalDeviceMerger.Merge(devices, Priority).Devices.Count);
    }

    [Fact]
    public void Single_gpu_merges_on_pci_ids_when_unambiguous()
    {
        var devices = new[]
        {
            Dev("lhm:/gpu-nvidia/0", "NVIDIA GeForce RTX 3080", "lhm", new DeviceIdentity { VendorId = 0x10DE, DeviceId = 0x2206, SubsystemVendorId = 0x1462, SubsystemId = 0x3897 }),
            Dev("nvapi:gpu0", "NVIDIA GeForce RTX 3080", "nvapi", new DeviceIdentity { Location = NvBus, VendorId = 0x10DE, DeviceId = 0x2206, SubsystemVendorId = 0x1462, SubsystemId = 0x3897 }),
        };
        Assert.Single(PhysicalDeviceMerger.Merge(devices, Priority).Devices);

        // Different subsystem ids → different boards → not merged.
        var other = devices[1] with { Identity = devices[1].Identity! with { SubsystemId = 0x1111 } };
        Assert.Equal(2, PhysicalDeviceMerger.Merge([devices[0], other], Priority).Devices.Count);
    }

    [Fact]
    public void Same_name_alone_never_merges()
    {
        var devices = new[] { Dev("lhm:x", "Generic GPU", "lhm", null), Dev("nv:x", "Generic GPU", "nvapi", null) };
        Assert.Equal(2, PhysicalDeviceMerger.Merge(devices, Priority).Devices.Count);
    }

    [Fact]
    public void Memory_singleton_merges_across_providers_and_parents_are_remapped()
    {
        var devices = new[]
        {
            Dev("lhm:/ram", "Total Memory", "lhm", new DeviceIdentity { SingletonKey = "system-memory" }, HardwareCategory.Memory),
            Dev("lhm:/vram", "Virtual Memory", "lhm", null, HardwareCategory.Memory),
            Dev("win:memory", "System Memory", "windows", new DeviceIdentity { SingletonKey = "system-memory" }, HardwareCategory.Memory),
            new HardwareDevice("lhm:/sio", "Nuvoton", HardwareCategory.SuperIo, "lhm", ParentDeviceId: "lhm:/mainboard"),
            Dev("lhm:/mainboard", "Board", "lhm", null, HardwareCategory.Motherboard),
        };
        var result = PhysicalDeviceMerger.Merge(devices, Priority);
        Assert.Equal(4, result.Devices.Count);
        var mem = result.Devices.Single(d => d.DeviceId == "sys:system-memory");
        Assert.Equal("Total Memory", mem.Name); // highest-priority provider names the device
        Assert.Equal("lhm:/mainboard", result.Devices.Single(d => d.Name == "Nuvoton").ParentDeviceId);
    }

    [Fact]
    public void Duplicate_measurements_are_suppressed_in_favour_of_priority_provider()
    {
        var map = new Dictionary<string, string> { ["lhm:g"] = "pci:x", ["nv:g"] = "pci:x" };
        var raw = new[]
        {
            new RawSensorReading("lhm:t", "lhm:g", "GPU Core", SensorType.Temperature, SensorUnit.Celsius, 60, "lhm"),
            new RawSensorReading("nv:t", "nv:g", "GPU Core", SensorType.Temperature, SensorUnit.Celsius, 60, "nvapi"),
            new RawSensorReading("nv:c", "nv:g", "GPU Core", SensorType.Clock, SensorUnit.Megahertz, 1900, "nvapi"),
            new RawSensorReading("nv:p", "nv:g", "Power Limit (enforced)", SensorType.Power, SensorUnit.Watt, 320, "nvapi"),
        };
        var (suppressed, also) = PhysicalDeviceMerger.FindDuplicateSensors(raw, map, Priority);
        Assert.Equal(["nv:t"], suppressed);
        Assert.Equal(["nvapi"], also["lhm:t"]);
    }

    [Fact]
    public void Polling_service_produces_one_gpu_with_merged_sensors()
    {
        var lhm = new FakeProvider("lhm", 0).WithDevice("lhm:/gpu-nvidia/0", HardwareCategory.Gpu, "NVIDIA GeForce RTX 3080", PnpPath.FromPath(NvPath))
            .Set("lhm:t", "lhm:/gpu-nvidia/0", "GPU Core", SensorType.Temperature, 61)
            .Set("lhm:h", "lhm:/gpu-nvidia/0", "GPU Hot Spot", SensorType.Temperature, 72);
        var nv = new FakeProvider("nvapi", 10).WithDevice("nvapi:gpu0", HardwareCategory.Gpu, "NVIDIA GeForce RTX 3080", new DeviceIdentity { Location = NvBus, VendorId = 0x10DE, DeviceId = 0x2206 })
            .Set("nv:t", "nvapi:gpu0", "GPU Core", SensorType.Temperature, 61)
            .Set("nv:lim", "nvapi:gpu0", "Hardware Thermal Slowdown", SensorType.PerformanceLimit, 0)
            .Set("nv:thr", "nvapi:gpu0", "GPU Slowdown Temperature Threshold", SensorType.Temperature, 87);
        var svc = new SensorPollingService([nv, lhm], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance, null,
            new MapResolver(new() { [NvPath] = NvBus }));
        var snap = svc.PollOnce();

        var gpu = Assert.Single(snap.Devices);
        Assert.Equal("pci:0000:01:00.0", gpu.DeviceId);
        Assert.All(snap.Sensors, s => Assert.Equal(gpu.DeviceId, s.DeviceId));
        Assert.Null(snap.Find("nv:t")); // duplicate of lhm:t
        Assert.Equal(["nvapi"], snap.Find("lhm:t")!.AlsoReportedBy);
        Assert.NotNull(snap.Find("nv:lim"));
        Assert.Equal(SensorKind.Metadata, snap.Find("nv:thr")!.Kind);
        Assert.Equal(72, snap.MaxProcessorTemperature()); // the 87 °C threshold is not a reading
        Assert.Equal(4, snap.Sensors.Count);
    }

    [Fact]
    public void Statistics_survive_device_merging_and_re_merging()
    {
        var lhm = new FakeProvider("lhm", 0).WithDevice("lhm:g", HardwareCategory.Gpu, "GPU", new DeviceIdentity { Location = NvBus })
            .Set("lhm:t", "lhm:g", "GPU Core", SensorType.Temperature, 50);
        var svc = new SensorPollingService([lhm], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
        svc.PollOnce();
        lhm.Set("lhm:t", "lhm:g", "GPU Core", SensorType.Temperature, 70);
        lhm.WithDevice("lhm:extra", HardwareCategory.Storage, "Disk"); // device set changes → merge recomputed
        var r = svc.PollOnce().Find("lhm:t")!;
        Assert.Equal((50d, 70d, 2L), (r.Minimum!.Value, r.Maximum!.Value, r.SampleCount));
    }
}

public class PnpIdentityResolverTests
{
    [Fact]
    public void Maps_instance_path_to_location_and_back()
    {
        const string path = @"PCI\VEN_10DE&DEV_2206&SUBSYS_38971462&REV_A1\4&2283F625&0&0019";
        var table = new Dictionary<string, PciDeviceProperties>
        {
            [path] = new(path, BusNumber: 1, Address: (0u << 16) | 0, 4, 16, 4, 16, null, null),
        };
        var resolver = new PnpIdentityResolver(() => table);
        Assert.Equal(new PciLocation(0, 1, 0, 0), resolver.Resolve(new DeviceIdentity { InstancePath = path }).Location);
        var back = resolver.Resolve(new DeviceIdentity { Location = new PciLocation(0, 1, 0, 0) });
        Assert.Equal(path, back.InstancePath);
        Assert.Equal((ushort)0x2206, back.DeviceId);
        Assert.Null(resolver.Resolve(new DeviceIdentity { InstancePath = "PCI\\VEN_1234&DEV_0001\\X" }).Location);
    }

    [Fact]
    public void Inventory_never_merges_identical_gpus_with_different_locations()
    {
        var merged = HardwareInventoryService.Merge(
        [
            new DeviceInfo { Id = "a", Name = "RTX 3080", Category = HardwareCategory.Gpu, Source = "PCI", PhysicalKey = "pci:0000:01:00.0", Properties = [new("Bus", "1")] },
            new DeviceInfo { Id = "b", Name = "RTX 3080", Category = HardwareCategory.Gpu, Source = "PCI", PhysicalKey = "pci:0000:02:00.0", Properties = [new("Bus", "2")] },
            new DeviceInfo { Id = "n", Name = "RTX 3080", Category = HardwareCategory.Gpu, Source = "NVML", PhysicalKey = "pci:0000:02:00.0", Properties = [new("VBIOS version", "x")] },
            new DeviceInfo { Id = "w", Name = "RTX 3080", Category = HardwareCategory.Gpu, Source = "WMI", Properties = [new("Orphan", "y")] },
        ]);
        Assert.Equal(3, merged.Count); // two physical GPUs + an ambiguous keyless entry kept separately
        Assert.Contains(merged, d => d.PhysicalKey == "pci:0000:02:00.0" && d.Properties.Any(p => p.Name == "VBIOS version") && d.Source == "PCI, NVML");
    }

    [Fact]
    public void Keyless_entry_joins_the_single_keyed_device_and_replaces_not_available()
    {
        var merged = HardwareInventoryService.Merge(
        [
            new DeviceInfo { Id = "a", Name = "RTX 3080", Category = HardwareCategory.Gpu, Source = "PCI", PhysicalKey = "pci:0000:01:00.0",
                Properties = [HardwareProperty.Unavailable("VBIOS version", PropertySections.Firmware, "registry empty")] },
            new DeviceInfo { Id = "w", Name = "RTX 3080", Category = HardwareCategory.Gpu, Source = "NVML", Properties = [new("VBIOS version", "94.02", PropertySections.Firmware, "NVML")] },
        ]);
        var gpu = Assert.Single(merged);
        Assert.Equal("94.02", gpu.Properties.Single(p => p.Name == "VBIOS version").Value);
    }
}
