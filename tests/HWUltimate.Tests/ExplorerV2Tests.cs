using HWUltimate.Core.Explorer;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Tests;

public class ExplorerV2Tests
{
    private static readonly HardwareDevice Cpu = new("cpu", "Ryzen 9", HardwareCategory.Cpu, "lhm");
    private static readonly HardwareDevice Gpu = new("gpu", "RTX 3080", HardwareCategory.Gpu, "lhm") { Providers = ["lhm", "nvapi"] };
    private static readonly HardwareDevice Gpu2 = new("igpu", "Radeon Graphics", HardwareCategory.Gpu, "lhm");
    private static readonly HardwareDevice Dimm = new("dimm", "DIMM #1", HardwareCategory.MemoryModule, "lhm");
    private static readonly HardwareDevice Ssd = new("ssd", "990 PRO", HardwareCategory.Storage, "lhm");

    private static SensorSnapshot Snapshot() => Snapshots.Of(DateTimeOffset.Now, [Cpu, Gpu, Gpu2, Dimm, Ssd],
        Snapshots.Reading("c-t", "cpu", "CPU Package", SensorType.Temperature, 61) with { Index = 0 },
        Snapshots.Reading("c-p", "cpu", "CPU Package", SensorType.Power, 90) with { Index = 1 },
        Snapshots.Reading("c-l", "cpu", "CPU Total", SensorType.Utilization, 20) with { Index = 2 },
        Snapshots.Reading("g-t", "gpu", "GPU Core", SensorType.Temperature, 75),
        Snapshots.Reading("g-p", "gpu", "GPU Power", SensorType.Power, 300) with { ProviderName = "nvapi" },
        Snapshots.Reading("g-l", "gpu", "GPU Core", SensorType.Utilization, 99),
        Snapshots.Reading("i-t", "igpu", "GPU Core", SensorType.Temperature, 45),
        Snapshots.Reading("d-t", "dimm", "DIMM Temperature", SensorType.Temperature, 40),
        Snapshots.Reading("s-t", "ssd", "Composite Temperature", SensorType.Temperature, 48) with { Index = 0 },
        Snapshots.Reading("s-w", "ssd", "Warning Composite Temperature", SensorType.Temperature, 82, kind: SensorKind.Metadata) with { Index = 1 },
        Snapshots.Reading("s-x", "ssd", "Read Activity", SensorType.Utilization, null, DataQuality.Missing),
        Snapshots.Reading("s-q", "ssd", "Weird", SensorType.Utilization, 250, DataQuality.Suspect));

    private static List<ExplorerNode> Build(SensorExplorerState state, ExplorerFilter? f = null) => SensorExplorerBuilder.Build(Snapshot(), state, f).ToList();

    private static List<string> Ids(IEnumerable<ExplorerNode> nodes) => nodes.Where(n => n.Kind == ExplorerNodeKind.Sensor).Select(n => n.Sensor!.SensorId).ToList();

    [Fact]
    public void Metadata_is_hidden_in_compact_and_shown_in_its_own_group_in_advanced()
    {
        Assert.DoesNotContain("s-w", Ids(Build(new SensorExplorerState())));
        var advanced = Build(new SensorExplorerState { DisplayMode = ExplorerDisplayMode.Advanced }, new ExplorerFilter(IncludeMetadata: true));
        var node = advanced.Single(n => n.Sensor?.SensorId == "s-w");
        Assert.Equal(SensorGroupKind.Metadata, node.Group);
        // The live Temperatures group of the drive only has the real reading.
        Assert.Equal(["s-t"], advanced.Where(n => n.GroupKey == ExplorerKeys.Group("ssd", SensorGroupKind.Temperatures) && n.Kind == ExplorerNodeKind.Sensor).Select(n => n.Sensor!.SensorId));
    }

    [Fact]
    public void Memory_modules_have_their_own_category()
    {
        var cats = Build(new SensorExplorerState()).Where(n => n.Kind == ExplorerNodeKind.Category).Select(n => n.Category).ToList();
        Assert.Equal([HardwareCategory.Cpu, HardwareCategory.Gpu, HardwareCategory.MemoryModule, HardwareCategory.Storage], cats);
    }

    [Fact]
    public void Filters_by_device_provider_and_status()
    {
        Assert.Equal(["i-t"], Ids(Build(new SensorExplorerState(), new ExplorerFilter(DeviceId: "igpu"))));
        Assert.Equal(["g-p"], Ids(Build(new SensorExplorerState(), new ExplorerFilter(Provider: "nvapi"))));
        Assert.Equal(["s-x"], Ids(Build(new SensorExplorerState(), new ExplorerFilter(Status: SensorStatusFilter.NoData))));
        Assert.Equal(["s-q"], Ids(Build(new SensorExplorerState(), new ExplorerFilter(Status: SensorStatusFilter.Attention))));
        Assert.DoesNotContain("s-x", Ids(Build(new SensorExplorerState(), new ExplorerFilter(Status: SensorStatusFilter.Reporting))));

        var state = new SensorExplorerState();
        state.SetHidden("c-l", true);
        Assert.Equal(["c-l"], Ids(Build(state, new ExplorerFilter(Status: SensorStatusFilter.Hidden))));
        state.SetPinned("g-t", true);
        Assert.Equal(["g-t"], Ids(Build(state, new ExplorerFilter(Status: SensorStatusFilter.Pinned))));
    }

    [Fact]
    public void Pinned_sensors_stay_first_in_their_group_under_any_sort()
    {
        var state = new SensorExplorerState();
        var group = ExplorerKeys.Group("ssd", SensorGroupKind.Utilization);
        state.SetPinned("s-q", true);
        foreach (var sort in Enum.GetValues<SensorSortColumn>())
        {
            state.SortColumn = sort;
            Assert.Equal("s-q", SensorExplorerBuilder.GroupOrder(Build(state), group)[0]);
        }

        Assert.True(Build(state).Single(n => n.Sensor?.SensorId == "s-q").IsPinned);
        state.TogglePinned("s-q");
        Assert.False(state.IsPinned("s-q"));
    }

    [Fact]
    public void Sort_by_temperature_orders_devices_by_hottest_reading()
    {
        var state = new SensorExplorerState { SortColumn = SensorSortColumn.Temperature };
        var gpus = Build(state).Where(n => n.Kind == ExplorerNodeKind.Device && n.Category == HardwareCategory.Gpu).Select(n => n.DeviceId).ToList();
        Assert.Equal(["gpu", "igpu"], gpus);
    }

    [Fact]
    public void Sort_by_power_and_utilization_put_matching_sensors_first()
    {
        var sensors = Snapshot().Sensors.Where(s => s.DeviceId == "cpu").ToList();
        var byPower = SensorExplorerBuilder.Sort(sensors, "g", new SensorExplorerState { SortColumn = SensorSortColumn.Power });
        Assert.Equal("c-p", byPower[0].SensorId);
        var byLoad = SensorExplorerBuilder.Sort(sensors, "g", new SensorExplorerState { SortColumn = SensorSortColumn.Utilization });
        Assert.Equal("c-l", byLoad[0].SensorId);
        var byMax = SensorExplorerBuilder.Sort(sensors, "g", new SensorExplorerState { SortColumn = SensorSortColumn.Value, SortDescending = true });
        Assert.Equal("c-p", byMax[0].SensorId);
    }

    [Fact]
    public void Compact_is_default_and_v01_detailed_mode_maps_to_advanced()
    {
        Assert.Equal(ExplorerDisplayMode.Compact, new SensorExplorerState().DisplayMode);
        Assert.Equal(ExplorerDisplayMode.Advanced, ExplorerDisplayMode.Detailed);
        var json = System.Text.Json.JsonSerializer.Deserialize<SensorExplorerState>("{\"DisplayMode\":\"Detailed\",\"PinnedSensors\":[\"a\"]}",
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!.Sanitize();
        Assert.Equal(ExplorerDisplayMode.Advanced, json.DisplayMode);
        Assert.True(json.IsPinned("a"));
    }

    [Fact]
    public void Advanced_columns_can_be_enabled()
    {
        var state = new SensorExplorerState();
        state.SetColumnVisible(ExplorerColumns.SensorId, true);
        state.SetColumnVisible(ExplorerColumns.Samples, true);
        Assert.Equal([ExplorerColumns.Value, ExplorerColumns.Minimum, ExplorerColumns.Maximum, ExplorerColumns.Average, ExplorerColumns.SensorId, ExplorerColumns.Samples], state.VisibleColumns);
    }

    [Fact]
    public void Device_nodes_carry_provider_information()
    {
        var gpu = Build(new SensorExplorerState()).Single(n => n.Key == ExplorerKeys.Device("gpu"));
        Assert.Equal(["lhm", "nvapi"], gpu.Device!.Providers);
    }
}
