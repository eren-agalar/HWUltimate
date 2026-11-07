using Microsoft.Extensions.Logging.Abstractions;
using HWUltimate.Core.Abstractions;
using HWUltimate.Core.Detection;
using HWUltimate.Core.Explorer;
using HWUltimate.Core.Identity;
using HWUltimate.Core.Sensors;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;
using HWUltimate.Infrastructure.Smbios;

namespace HWUltimate.Tests;

/// <summary>A provider whose detection can be repeated; the second scan "finds" the motherboard chip.</summary>
internal sealed class RescannableFake : IHardwareProvider, IRescannableProvider
{
    public string Name => "fake-lhm";
    public int Priority => 0;
    public ProviderStatus Status { get; set; } = ProviderStatus.Ready();
    public List<HardwareDevice> Devices { get; } = [new("cpu", "CPU", HardwareCategory.Cpu, "fake-lhm")];
    public List<RawSensorReading> Readings { get; } = [new("t", "cpu", "CPU Package", SensorType.Temperature, SensorUnit.Celsius, 50, "fake-lhm")];
    public List<bool> RescanCalls { get; } = [];

    public Task<RescanResult> RescanAsync(bool force, CancellationToken cancellationToken = default)
    {
        RescanCalls.Add(force);
        Devices.Add(new HardwareDevice("sio", "Super I/O", HardwareCategory.SuperIo, "fake-lhm"));
        Readings.Add(new RawSensorReading("f", "sio", "Fan #1", SensorType.FanSpeed, SensorUnit.Rpm, 900, "fake-lhm"));
        return Task.FromResult(new RescanResult(true, "found Super I/O"));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Update() { }
    public IReadOnlyList<RawSensorReading> ReadSensors() => Readings.ToList();
    public IReadOnlyList<HardwareDevice> GetDevices() => Devices.ToList();
    public void Dispose() { }
}

public class RescanTests
{
    [Fact]
    public async Task Rescan_adds_sensors_found_later_without_restart()
    {
        var provider = new RescannableFake();
        var svc = new SensorPollingService([provider], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
        await svc.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Single(svc.PollOnce().Sensors);

        var changes = await svc.RescanAsync(TestContext.Current.CancellationToken);
        Assert.Equal([true], provider.RescanCalls); // user-requested re-scan is forced
        Assert.Single(changes);
        var after = svc.PollOnce();
        Assert.Equal(2, after.Sensors.Count);
        Assert.NotNull(after.Find("f"));
    }

    [Fact]
    public async Task Periodic_retry_skips_providers_that_work()
    {
        var provider = new RescannableFake();
        var svc = new SensorPollingService([provider], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
        await svc.RescanAsync(RescanMode.UnavailableOnly, TestContext.Current.CancellationToken);
        Assert.Empty(provider.RescanCalls);

        provider.Status = ProviderStatus.Unavailable("driver not loaded yet");
        await svc.RescanAsync(RescanMode.UnavailableOnly, TestContext.Current.CancellationToken);
        Assert.Equal([false], provider.RescanCalls);
    }

    [Fact]
    public void Detection_rules_are_generic()
    {
        var complete = new DetectionRules.DetectionSummary(12, 140, IsElevated: true, LowLevelDriverAvailable: true, HasMotherboard: true, HasMotherboardChip: true, HasCpu: true, CpuHasTemperature: true);
        Assert.False(DetectionRules.LooksIncomplete(complete));
        Assert.True(DetectionRules.LooksIncomplete(complete with { HasMotherboardChip = false }));
        Assert.True(DetectionRules.LooksIncomplete(complete with { CpuHasTemperature = false }));
        Assert.True(DetectionRules.LooksIncomplete(complete with { HardwareNodes = 0 }));
        // Without admin rights or the driver, a re-scan cannot find more: not "incomplete".
        Assert.False(DetectionRules.LooksIncomplete(complete with { IsElevated = false, HasMotherboardChip = false }));
        Assert.False(DetectionRules.LooksIncomplete(complete with { LowLevelDriverAvailable = false, CpuHasTemperature = false }));

        Assert.True(DetectionRules.IsBetter(complete with { HardwareNodes = 13 }, complete));
        Assert.True(DetectionRules.IsBetter(complete with { Sensors = 141 }, complete));
        Assert.False(DetectionRules.IsBetter(complete with { HardwareNodes = 11, Sensors = 500 }, complete));
    }

    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Controller I225-V", true, false, true, false, DeviceRelevance.Primary)]
    [InlineData("Wi-Fi", "Realtek RTL8852BE WiFi 6 802.11ax PCIe Adapter", true, false, true, false, DeviceRelevance.Primary)]
    [InlineData("Ethernet 2", "Realtek PCIe 2.5GbE Family Controller", false, false, true, false, DeviceRelevance.Inactive)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", true, false, false, true, DeviceRelevance.Virtual)]
    [InlineData("Local Area Connection* 1", "Microsoft Wi-Fi Direct Virtual Adapter", false, false, false, true, DeviceRelevance.Virtual)]
    [InlineData("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", true, true, null, null, DeviceRelevance.Virtual)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", false, false, null, null, DeviceRelevance.Virtual)]
    [InlineData("OpenVPN Connect DCO Adapter", "OpenVPN Data Channel Offload", true, false, null, null, DeviceRelevance.Virtual)]
    public void Network_adapters_are_classified_from_driver_flags_then_names(string name, string description, bool up, bool loopback, bool? physical, bool? virtualFlag, DeviceRelevance expected) =>
        Assert.Equal(expected, DetectionRules.ClassifyNetworkAdapter(new NetworkAdapterFacts(name, description, up, loopback, physical, virtualFlag)));

    [Fact]
    public void Unknown_adapter_facts_never_hide_an_adapter() =>
        Assert.Equal(DeviceRelevance.Primary, DetectionRules.ClassifyNetworkAdapter(new NetworkAdapterFacts("Ethernet")));

    [Fact]
    public void Virtual_and_disconnected_devices_appear_only_with_show_all()
    {
        var eth = new HardwareDevice("eth", "Ethernet", HardwareCategory.Network, "lhm");
        var wsl = new HardwareDevice("wsl", "vEthernet (WSL)", HardwareCategory.Network, "lhm") { Relevance = DeviceRelevance.Virtual };
        var off = new HardwareDevice("eth2", "Ethernet 2", HardwareCategory.Network, "lhm") { Relevance = DeviceRelevance.Inactive };
        var snap = Snapshots.Of(DateTimeOffset.Now, [eth, wsl, off],
            Snapshots.Reading("e-d", "eth", "Download Speed", SensorType.Throughput, 1000),
            Snapshots.Reading("w-d", "wsl", "Download Speed", SensorType.Throughput, 0),
            Snapshots.Reading("o-d", "eth2", "Download Speed", SensorType.Throughput, 0));
        Assert.Equal(["eth"], SensorExplorerBuilder.Build(snap, new SensorExplorerState()).Where(n => n.Kind == ExplorerNodeKind.Device).Select(n => n.DeviceId!));
        Assert.Equal(3, SensorExplorerBuilder.Build(snap, new SensorExplorerState { ShowAllSensors = true }).Count(n => n.Kind == ExplorerNodeKind.Device));
    }
}

public class NoiseRuleTests
{
    private static SensorReading R(string name, SensorType type, double? value, double? max, long samples = 10, DataQuality q = DataQuality.Good) =>
        Snapshots.Reading(name, "d", name, type, value, q) with { Maximum = max, SampleCount = samples };

    [Fact]
    public void Unwired_motherboard_inputs_are_secondary()
    {
        Assert.True(SensorNoiseRules.IsSecondary(R("Fan #5", SensorType.FanSpeed, 0, 0), HardwareCategory.SuperIo, SensorGroupKind.Fans));
        Assert.False(SensorNoiseRules.IsSecondary(R("Fan #1", SensorType.FanSpeed, 850, 1200), HardwareCategory.SuperIo, SensorGroupKind.Fans));
        Assert.True(SensorNoiseRules.IsSecondary(R("Voltage #7", SensorType.Voltage, 0, 0), HardwareCategory.SuperIo, SensorGroupKind.Voltages));
        Assert.True(SensorNoiseRules.IsSecondary(R("Temperature #6", SensorType.Temperature, 127, 127, q: DataQuality.Suspect), HardwareCategory.SuperIo, SensorGroupKind.Temperatures));
        Assert.True(SensorNoiseRules.IsSecondary(R("Temperature #4", SensorType.Temperature, 0, 0), HardwareCategory.EmbeddedController, SensorGroupKind.Temperatures));
        Assert.False(SensorNoiseRules.IsSecondary(R("System", SensorType.Temperature, 34, 36), HardwareCategory.SuperIo, SensorGroupKind.Temperatures));
    }

    [Fact]
    public void Gpu_zero_rpm_fans_stay_visible()
    {
        // Many graphics cards stop their fans at idle; that is a real reading, not an empty header.
        Assert.False(SensorNoiseRules.IsSecondary(R("GPU Fan 1", SensorType.FanSpeed, 0, 0), HardwareCategory.Gpu, SensorGroupKind.Fans));
    }

    [Fact]
    public void Never_reported_and_redundant_sensors_are_secondary_but_stale_ones_are_not()
    {
        Assert.True(SensorNoiseRules.IsSecondary(R("GPU Video Engine", SensorType.Utilization, null, null, samples: 0), HardwareCategory.Gpu, SensorGroupKind.Utilization));
        Assert.False(SensorNoiseRules.IsSecondary(R("GPU Core", SensorType.Temperature, null, 60, q: DataQuality.Stale), HardwareCategory.Gpu, SensorGroupKind.Temperatures));
        Assert.True(SensorNoiseRules.IsSecondary(R("Core #1 Distance to TjMax", SensorType.Temperature, 40, 60), HardwareCategory.Cpu, SensorGroupKind.Throttling));
        Assert.True(SensorNoiseRules.IsSecondary(R("Data Uploaded", SensorType.Memory, 3.2, 3.2), HardwareCategory.Network, SensorGroupKind.Data));
        Assert.False(SensorNoiseRules.IsSecondary(R("Upload Speed", SensorType.Throughput, 2000, 9000), HardwareCategory.Network, SensorGroupKind.Throughput));
    }

    [Theory]
    [InlineData(4, false)]   // quad-core, Intel naming
    [InlineData(64, false)]  // 64-core workstation
    [InlineData(8, true)]    // hybrid P/E naming
    public void Every_core_temperature_is_listed_under_temperatures(int cores, bool hybrid)
    {
        var cpu = new HardwareDevice("cpu", "CPU", HardwareCategory.Cpu, "lhm");
        var readings = new List<SensorReading> { Snapshots.Reading("pkg", "cpu", "CPU Package", SensorType.Temperature, 60) };
        for (var i = 1; i <= cores; i++)
        {
            var name = hybrid ? (i <= cores / 2 ? $"P-Core #{i}" : $"E-Core #{i - cores / 2}") : $"CPU Core #{i}";
            readings.Add(Snapshots.Reading($"t{i}", "cpu", name, SensorType.Temperature, 50 + i % 10));
            readings.Add(Snapshots.Reading($"c{i}", "cpu", name, SensorType.Clock, 4000));
        }

        var nodes = SensorExplorerBuilder.Build(Snapshots.Of(DateTimeOffset.Now, [cpu], [.. readings]), new SensorExplorerState());
        var temps = nodes.Where(n => n.Kind == ExplorerNodeKind.Sensor && n.Group == SensorGroupKind.Temperatures).ToList();
        Assert.Equal(cores + 1, temps.Count);
        Assert.Equal(cores, nodes.Count(n => n.Kind == ExplorerNodeKind.Sensor && n.Group == SensorGroupKind.Clocks));
        Assert.DoesNotContain(nodes, n => n.Group == SensorGroupKind.PerCore);
    }
}

public class PersistentHidingTests
{
    private static readonly HardwareDevice Nvme0 = new("lhm:/nvme/0", "Samsung SSD 990 PRO 2TB", HardwareCategory.Storage, "lhm");
    private static readonly HardwareDevice Nvme1 = new("lhm:/nvme/1", "WD_BLACK SN850X 1TB", HardwareCategory.Storage, "lhm");

    private static SensorSnapshot Boot(bool swapped)
    {
        // Drives enumerate in a different order on the next boot: their ids swap.
        var (a, b) = swapped ? ("1", "0") : ("0", "1");
        var samsung = Nvme0 with { DeviceId = $"lhm:/nvme/{a}" };
        var wd = Nvme1 with { DeviceId = $"lhm:/nvme/{b}" };
        return Snapshots.Of(DateTimeOffset.Now, [samsung, wd],
            Snapshots.Reading($"lhm:/nvme/{a}/temperature/0", samsung.DeviceId, "Composite Temperature", SensorType.Temperature, 40),
            Snapshots.Reading($"lhm:/nvme/{b}/temperature/0", wd.DeviceId, "Composite Temperature", SensorType.Temperature, 45));
    }

    [Fact]
    public void Hidden_sensor_follows_a_drive_whose_id_changed()
    {
        var first = Boot(swapped: false);
        var state = new SensorExplorerState();
        var samsungTemp = first.Find("lhm:/nvme/0/temperature/0")!;
        state.SetHidden(samsungTemp.SensorId, true, SensorExplorerState.FingerprintOf(samsungTemp, Nvme0.Name));

        // Next boot, only the Samsung present at first (other drive slow to appear): ids changed.
        var second = Boot(swapped: true);
        var partial = Snapshots.Of(DateTimeOffset.Now, [second.Devices[0]], second.Sensors[0]);
        Assert.True(state.Rebind(partial));
        Assert.True(state.IsHidden("lhm:/nvme/1/temperature/0"));
        Assert.False(state.IsHidden("lhm:/nvme/0/temperature/0"));

        // The WD drive (now nvme/0) stays visible.
        var ids = SensorExplorerBuilder.Build(second, state).Where(n => n.Kind == ExplorerNodeKind.Sensor).Select(n => n.Sensor!.SensorId).ToList();
        Assert.Equal(["lhm:/nvme/0/temperature/0"], ids);
    }

    [Fact]
    public void Hidden_sensor_follows_a_swap_when_both_drives_are_present()
    {
        var state = new SensorExplorerState();
        var first = Boot(swapped: false);
        var wdTemp = first.Find("lhm:/nvme/1/temperature/0")!;
        state.SetHidden(wdTemp.SensorId, true, SensorExplorerState.FingerprintOf(wdTemp, Nvme1.Name));
        var second = Boot(swapped: true);
        Assert.True(state.Rebind(second));
        Assert.True(state.IsHidden("lhm:/nvme/0/temperature/0"));
        Assert.False(state.IsHidden("lhm:/nvme/1/temperature/0"));
    }

    [Fact]
    public void Ambiguous_matches_are_left_alone()
    {
        var a = new HardwareDevice("d0", "Same Drive", HardwareCategory.Storage, "lhm");
        var b = new HardwareDevice("d1", "Same Drive", HardwareCategory.Storage, "lhm");
        var snap = Snapshots.Of(DateTimeOffset.Now, [a, b],
            Snapshots.Reading("x0", "d0", "Temperature", SensorType.Temperature, 40),
            Snapshots.Reading("x1", "d1", "Temperature", SensorType.Temperature, 41));
        var state = new SensorExplorerState();
        state.SetHidden("gone", true, "Same Drive|Temperature|Temperature");
        Assert.False(state.Rebind(snap));
        Assert.True(state.IsHidden("gone"));
    }

    [Fact]
    public void Hiding_a_sensor_also_hides_its_duplicates_from_other_providers()
    {
        var state = new SensorExplorerState();
        state.SetHidden("lhm:gpu/temp", true, "GPU|Temperature|GPU Core", ["nvapi:gpu0/temp"]);
        Assert.True(state.IsHidden("nvapi:gpu0/temp"));
    }

    [Fact]
    public void Whole_devices_and_groups_can_be_hidden_and_follow_id_changes()
    {
        var state = new SensorExplorerState();
        state.SetNodeHidden(ExplorerKeys.Device("lhm:/nvme/1"), true, Nvme1.Name);
        state.SetNodeHidden(ExplorerKeys.Group("lhm:/nvme/0", SensorGroupKind.Temperatures), true, Nvme0.Name);
        var first = Boot(swapped: false);
        Assert.DoesNotContain(SensorExplorerBuilder.Build(first, state), n => n.Kind == ExplorerNodeKind.Sensor);

        // Next boot the drives swap numbers; both are present, so only the fingerprints reveal the swap.
        var second = Boot(swapped: true);
        Assert.True(state.Rebind(second));
        Assert.True(state.IsNodeHidden(ExplorerKeys.Device("lhm:/nvme/0")));                                  // WD is now nvme/0
        Assert.True(state.IsNodeHidden(ExplorerKeys.Group("lhm:/nvme/1", SensorGroupKind.Temperatures)));     // Samsung is now nvme/1
        Assert.False(state.IsNodeHidden(ExplorerKeys.Device("lhm:/nvme/1")));
        Assert.DoesNotContain(SensorExplorerBuilder.Build(second, state), n => n.Kind == ExplorerNodeKind.Sensor);
        Assert.False(state.Rebind(second)); // stable once moved

        state.ShowHidden = true;
        var shown = SensorExplorerBuilder.Build(second, state);
        Assert.True(shown.Single(n => n.Key == ExplorerKeys.Device("lhm:/nvme/0")).IsHidden);
        Assert.True(shown.Single(n => n.Key == ExplorerKeys.Group("lhm:/nvme/1", SensorGroupKind.Temperatures)).IsHidden);
    }

    [Fact]
    public void Hidden_state_survives_a_restart_through_the_preference_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hwu-hidden-{Guid.NewGuid():N}.json");
        try
        {
            var store = new HWUltimate.Infrastructure.Preferences.JsonPreferenceStore<SensorExplorerState>(path, NullLogger.Instance);
            var state = new SensorExplorerState();
            state.SetHidden("s1", true, "Dev|Temperature|T1");
            state.SetNodeHidden(ExplorerKeys.Group("dev", SensorGroupKind.Voltages), true, "Dev");
            state.ShowAllSensors = true;
            store.Save(state);

            var loaded = new HWUltimate.Infrastructure.Preferences.JsonPreferenceStore<SensorExplorerState>(path, NullLogger.Instance).Load().Sanitize();
            Assert.True(loaded.IsHidden("s1"));
            Assert.True(loaded.IsNodeHidden(ExplorerKeys.Group("dev", SensorGroupKind.Voltages)));
            Assert.Equal("Dev|Temperature|T1", loaded.Fingerprints["s1"]);
            Assert.True(loaded.ShowAllSensors);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class DeviceNameTests
{
    private static SmbiosMemoryDevice Dimm(long mb, string part, string maker = "Kingston", string type = "DDR5", int speed = 6000, string slot = "DIMM_A2") =>
        new(slot, "BANK 0", mb, type, speed, speed, maker, "0001", part, 1350, "DIMM");

    private static SmbiosData Data(params SmbiosMemoryDevice[] modules) => new(new Version(3, 5), null, null, null, [], modules, 0);

    [Fact]
    public void Memory_title_from_smbios()
    {
        Assert.Equal("32 GB DDR5-6000 (2 × 16 GB) · Kingston KF560C36-16", DeviceNameResolver.MemoryTitle(Data(Dimm(16384, "KF560C36-16"), Dimm(16384, "KF560C36-16"))));
        Assert.Equal("24 GB DDR4-3200 (16 GB + 8 GB)", DeviceNameResolver.MemoryTitle(Data(Dimm(16384, "A", "Samsung", "DDR4", 3200), Dimm(8192, "B", "Micron", "DDR4", 3200))));
        // Placeholder strings some firmware writes are ignored rather than shown.
        Assert.Equal("8 GB DDR3-1600 (1 × 8 GB)", DeviceNameResolver.MemoryTitle(Data(Dimm(8192, "Unknown", "Undefined", "DDR3", 1600))));
        Assert.Null(DeviceNameResolver.MemoryTitle(Data(Dimm(0, "empty slot"))));
    }

    [Fact]
    public void System_memory_is_renamed_and_blank_names_get_a_category_name()
    {
        var resolver = new DeviceNameResolver(() => Data(Dimm(16384, "X"), Dimm(16384, "X")));
        var memory = new HardwareDevice("lhm:/ram", "Total Memory", HardwareCategory.Memory, "lhm", Identity: new DeviceIdentity { SingletonKey = "system-memory" });
        Assert.StartsWith("32 GB DDR5-6000", resolver.DisplayName(memory));
        Assert.Equal("Network adapter", resolver.DisplayName(new HardwareDevice("n", " ", HardwareCategory.Network, "lhm")));
        Assert.Null(resolver.DisplayName(new HardwareDevice("c", "Intel Core i7-12700K", HardwareCategory.Cpu, "lhm")));

        // No SMBIOS (e.g. some virtual machines): a plain name, never blank, never invented.
        Assert.Equal("System Memory", new DeviceNameResolver(() => null).DisplayName(memory));
    }

    [Fact]
    public async Task Polling_service_applies_resolved_names()
    {
        var p = new FakeProvider("lhm").WithDevice("ram", HardwareCategory.Memory, "Total Memory", new DeviceIdentity { SingletonKey = "system-memory" })
            .Set("u", "ram", "Memory Used", SensorType.Memory, 10);
        var svc = new SensorPollingService([p], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance,
            nameResolver: new DeviceNameResolver(() => Data(Dimm(16384, "X"))));
        await svc.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("16 GB DDR5-6000 (1 × 16 GB) · Kingston X", Assert.Single(svc.PollOnce().Devices).Name);
    }
}
