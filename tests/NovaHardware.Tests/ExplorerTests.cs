using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.Core.Explorer;
using NovaHardware.Core.Sensors;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using NovaHardware.Infrastructure.Preferences;

namespace NovaHardware.Tests;

public class SensorClassifierTests
{
    [Theory]
    [InlineData(SensorType.Temperature, "Core #3", HardwareCategory.Cpu, SensorGroupKind.PerCore)]
    [InlineData(SensorType.Clock, "CPU Core #12", HardwareCategory.Cpu, SensorGroupKind.PerCore)]
    [InlineData(SensorType.Temperature, "Core Average", HardwareCategory.Cpu, SensorGroupKind.Temperatures)]
    [InlineData(SensorType.Temperature, "CPU Package", HardwareCategory.Cpu, SensorGroupKind.Temperatures)]
    [InlineData(SensorType.Temperature, "Core #1 Distance to TjMax", HardwareCategory.Cpu, SensorGroupKind.Throttling)]
    [InlineData(SensorType.Temperature, "GPU Hot Spot", HardwareCategory.Gpu, SensorGroupKind.Hotspot)]
    [InlineData(SensorType.Temperature, "GPU Memory Junction", HardwareCategory.Gpu, SensorGroupKind.MemoryTemperatures)]
    [InlineData(SensorType.Temperature, "GPU Core", HardwareCategory.Gpu, SensorGroupKind.Temperatures)]
    [InlineData(SensorType.Clock, "GPU Memory", HardwareCategory.Gpu, SensorGroupKind.MemoryClocks)]
    [InlineData(SensorType.MemorySmall, "GPU Memory Used", HardwareCategory.Gpu, SensorGroupKind.Vram)]
    [InlineData(SensorType.Power, "Power Limit (enforced)", HardwareCategory.Gpu, SensorGroupKind.PowerLimits)]
    [InlineData(SensorType.PerformanceLimit, "Hardware Thermal Slowdown", HardwareCategory.Gpu, SensorGroupKind.PerformanceLimits)]
    [InlineData(SensorType.Utilization, "Read Activity", HardwareCategory.Storage, SensorGroupKind.Activity)]
    [InlineData(SensorType.Level, "Available Spare", HardwareCategory.Storage, SensorGroupKind.Health)]
    [InlineData(SensorType.Memory, "Data Written", HardwareCategory.Storage, SensorGroupKind.Data)]
    [InlineData(SensorType.Memory, "Memory Used", HardwareCategory.Memory, SensorGroupKind.Memory)]
    [InlineData(SensorType.Throughput, "Download Speed", HardwareCategory.Network, SensorGroupKind.Throughput)]
    [InlineData(SensorType.FanSpeed, "Fan #2", HardwareCategory.SuperIo, SensorGroupKind.Fans)]
    [InlineData(SensorType.Voltage, "Vcore", HardwareCategory.SuperIo, SensorGroupKind.Voltages)]
    public void Classifies_sensors(SensorType type, string name, HardwareCategory category, SensorGroupKind expected) =>
        Assert.Equal(expected, SensorClassifier.Classify(type, name, category));
}

public class SensorExplorerBuilderTests
{
    private static readonly HardwareDevice Cpu = new("cpu", "Ryzen 9", HardwareCategory.Cpu, "lhm");
    private static readonly HardwareDevice Gpu = new("gpu", "RTX 4090", HardwareCategory.Gpu, "lhm");
    private static readonly HardwareDevice Sio = new("sio", "Nuvoton NCT6798D", HardwareCategory.SuperIo, "lhm", "board");
    private static readonly HardwareDevice Board = new("board", "ASUS X670E", HardwareCategory.Motherboard, "lhm");

    internal static SensorSnapshot Snapshot() => Snapshots.Of(DateTimeOffset.Now, [Gpu, Cpu, Board, Sio],
        Snapshots.Reading("c-pkg", "cpu", "CPU Package", SensorType.Temperature, 61) with { Minimum = 40, Maximum = 80, Average = 55, Index = 0 },
        Snapshots.Reading("c-c10", "cpu", "Core #10", SensorType.Clock, 5100) with { Index = 1 },
        Snapshots.Reading("c-c2", "cpu", "Core #2", SensorType.Clock, 4900) with { Index = 2 },
        Snapshots.Reading("c-load", "cpu", "CPU Total", SensorType.Utilization, 12) with { Index = 3 },
        Snapshots.Reading("c-vcore", "cpu", "Core (SVI3)", SensorType.Voltage, null, DataQuality.Missing) with { Index = 4 },
        Snapshots.Reading("g-temp", "gpu", "GPU Core", SensorType.Temperature, 50),
        Snapshots.Reading("g-hot", "gpu", "GPU Hot Spot", SensorType.Temperature, 62),
        Snapshots.Reading("s-fan1", "sio", "Fan #1", SensorType.FanSpeed, 900) with { Index = 0 },
        Snapshots.Reading("s-fan2", "sio", "Fan #2", SensorType.FanSpeed, 1200) with { Index = 1 },
        Snapshots.Reading("s-fan3", "sio", "Fan #3", SensorType.FanSpeed, 700) with { Index = 2 });

    private static List<ExplorerNode> Build(SensorExplorerState state, ExplorerFilter? f = null) => SensorExplorerBuilder.Build(Snapshot(), state, f).ToList();

    private static List<string> SensorIds(IEnumerable<ExplorerNode> nodes) => nodes.Where(n => n.Kind == ExplorerNodeKind.Sensor).Select(n => n.Sensor!.SensorId).ToList();

    [Fact]
    public void Builds_category_device_group_sensor_hierarchy_in_category_order()
    {
        var nodes = Build(new SensorExplorerState());
        var categories = nodes.Where(n => n.Kind == ExplorerNodeKind.Category).Select(n => n.Category).ToList();
        Assert.Equal([HardwareCategory.Cpu, HardwareCategory.Gpu, HardwareCategory.Motherboard], categories); // Super I/O folds under Motherboard
        Assert.Equal(0, nodes[0].Depth);
        Assert.Equal(ExplorerNodeKind.Device, nodes[1].Kind);
        Assert.Equal(ExplorerNodeKind.Group, nodes[2].Kind);
        Assert.Equal(ExplorerNodeKind.Sensor, nodes[3].Kind);
        Assert.Equal(3, nodes[3].Depth);
        Assert.Equal(10, SensorIds(nodes).Count);
        // Board device has no sensors → not shown; Super I/O device appears under Motherboard.
        Assert.DoesNotContain(nodes, n => n.Key == ExplorerKeys.Device("board"));
        Assert.Contains(nodes, n => n.Key == ExplorerKeys.Device("sio"));
    }

    [Fact]
    public void Gpu_hotspot_gets_its_own_group()
    {
        var nodes = Build(new SensorExplorerState());
        Assert.Contains(nodes, n => n.Key == ExplorerKeys.Group("gpu", SensorGroupKind.Hotspot));
        Assert.Equal(SensorGroupKind.Hotspot, nodes.Single(n => n.Sensor?.SensorId == "g-hot").Group);
    }

    [Fact]
    public void Collapsing_a_node_hides_its_descendants_and_expand_restores_them()
    {
        var state = new SensorExplorerState();
        state.SetExpanded(ExplorerKeys.Device("cpu"), false);
        var nodes = Build(state);
        var cpuDevice = nodes.Single(n => n.Key == ExplorerKeys.Device("cpu"));
        Assert.False(cpuDevice.IsExpanded);
        Assert.Equal(5, cpuDevice.ChildCount);
        Assert.DoesNotContain("c-pkg", SensorIds(nodes));
        Assert.Contains("g-temp", SensorIds(nodes));

        state.Toggle(ExplorerKeys.Device("cpu"));
        Assert.Contains("c-pkg", SensorIds(Build(state)));
    }

    [Fact]
    public void Collapse_all_then_expand_all()
    {
        var state = new SensorExplorerState();
        state.CollapseAll(SensorExplorerBuilder.ContainerKeys(Snapshot()));
        var collapsed = Build(state);
        Assert.All(collapsed, n => Assert.Equal(ExplorerNodeKind.Category, n.Kind));
        Assert.All(collapsed, n => Assert.False(n.IsExpanded));

        state.ExpandAll();
        Assert.Equal(10, SensorIds(Build(state)).Count);
    }

    [Fact]
    public void Individual_group_collapse()
    {
        var state = new SensorExplorerState();
        state.SetExpanded(ExplorerKeys.Group("cpu", SensorGroupKind.PerCore), false);
        var ids = SensorIds(Build(state));
        Assert.DoesNotContain("c-c2", ids);
        Assert.Contains("c-pkg", ids);
    }

    [Fact]
    public void Hidden_sensors_are_excluded_unless_show_hidden()
    {
        var state = new SensorExplorerState();
        state.SetHidden("g-hot", true);
        Assert.DoesNotContain("g-hot", SensorIds(Build(state)));
        // A group whose only sensor is hidden disappears entirely.
        Assert.DoesNotContain(Build(state), n => n.Key == ExplorerKeys.Group("gpu", SensorGroupKind.Hotspot));

        state.ShowHidden = true;
        var node = Build(state).Single(n => n.Sensor?.SensorId == "g-hot");
        Assert.True(node.IsHidden);

        state.UnhideAll();
        state.ShowHidden = false;
        Assert.Contains("g-hot", SensorIds(Build(state)));
    }

    [Fact]
    public void Favorites_appear_first_in_user_order_and_stay_in_place()
    {
        var state = new SensorExplorerState();
        state.SetFavorite("g-temp", true);
        state.SetFavorite("c-pkg", true);
        state.SetFavorite("missing-sensor", true); // stale id is ignored, not shown
        var nodes = Build(state);
        Assert.Equal(ExplorerKeys.Favorites, nodes[0].Key);
        Assert.Equal(2, nodes[0].ChildCount);
        Assert.Equal(ExplorerKeys.FavoriteSensor("g-temp"), nodes[1].Key);
        Assert.Equal(ExplorerKeys.FavoriteSensor("c-pkg"), nodes[2].Key);
        Assert.True(nodes.Single(n => n.Key == ExplorerKeys.Sensor("c-pkg")).IsFavorite);

        Assert.True(state.Move(ExplorerKeys.Favorites, [], "c-pkg", -1));
        Assert.Equal(ExplorerKeys.FavoriteSensor("c-pkg"), Build(state)[1].Key);

        state.ToggleFavorite("c-pkg");
        Assert.False(state.IsFavorite("c-pkg"));
    }

    [Fact]
    public void Search_filters_and_auto_expands_collapsed_nodes()
    {
        var state = new SensorExplorerState();
        state.CollapseAll(SensorExplorerBuilder.ContainerKeys(Snapshot()));
        var nodes = Build(state, new ExplorerFilter("hot spot"));
        Assert.Equal(["g-hot"], SensorIds(nodes));
        Assert.DoesNotContain(nodes, n => n.Key == ExplorerKeys.Favorites);
    }

    [Fact]
    public void Search_by_device_name_shows_all_its_sensors()
    {
        var ids = SensorIds(Build(new SensorExplorerState(), new ExplorerFilter("nuvoton")));
        Assert.Equal(["s-fan1", "s-fan2", "s-fan3"], ids);
    }

    [Fact]
    public void Category_and_group_filters()
    {
        Assert.Equal(["g-temp"], SensorIds(Build(new SensorExplorerState(), new ExplorerFilter(Category: HardwareCategory.Gpu, Group: SensorGroupKind.Temperatures))));
        Assert.Equal(3, SensorIds(Build(new SensorExplorerState(), new ExplorerFilter(Category: HardwareCategory.Motherboard))).Count);
        Assert.Empty(SensorIds(Build(new SensorExplorerState(), new ExplorerFilter("no such sensor"))));
    }

    [Fact]
    public void Sorts_by_name_naturally()
    {
        var state = new SensorExplorerState { SortColumn = SensorSortColumn.Name };
        var perCore = Build(state).Where(n => n.Group == SensorGroupKind.PerCore && n.Kind == ExplorerNodeKind.Sensor).Select(n => n.Title).ToList();
        Assert.Equal(["Core #2", "Core #10"], perCore);
        state.SortDescending = true;
        perCore = Build(state).Where(n => n.Group == SensorGroupKind.PerCore && n.Kind == ExplorerNodeKind.Sensor).Select(n => n.Title).ToList();
        Assert.Equal(["Core #10", "Core #2"], perCore);
    }

    [Fact]
    public void Sorts_by_value_with_missing_values_last()
    {
        var fans = new SensorExplorerState { SortColumn = SensorSortColumn.Value };
        Assert.Equal(["s-fan3", "s-fan1", "s-fan2"], SensorIds(Build(fans, new ExplorerFilter(Category: HardwareCategory.Motherboard))));
        fans.SortDescending = true;
        Assert.Equal(["s-fan2", "s-fan1", "s-fan3"], SensorIds(Build(fans, new ExplorerFilter(Category: HardwareCategory.Motherboard))));

        var sensors = new List<SensorReading>
        {
            Snapshots.Reading("a", "d", "a", SensorType.Power, null),
            Snapshots.Reading("b", "d", "b", SensorType.Power, 5),
            Snapshots.Reading("c", "d", "c", SensorType.Power, 1),
        };
        foreach (var desc in new[] { false, true })
        {
            var sorted = SensorExplorerBuilder.Sort(sensors, "g", new SensorExplorerState { SortColumn = SensorSortColumn.Value, SortDescending = desc });
            Assert.Equal("a", sorted[^1].SensorId);
        }
    }

    [Fact]
    public void Custom_order_by_moving_sensors()
    {
        var state = new SensorExplorerState();
        var group = ExplorerKeys.Group("sio", SensorGroupKind.Fans);
        var order = SensorExplorerBuilder.GroupOrder(Build(state), group);
        Assert.Equal(["s-fan1", "s-fan2", "s-fan3"], order);

        Assert.True(state.Move(group, order, "s-fan3", -1));
        Assert.Equal(["s-fan1", "s-fan3", "s-fan2"], SensorExplorerBuilder.GroupOrder(Build(state), group));
        Assert.False(state.Move(group, SensorExplorerBuilder.GroupOrder(Build(state), group), "s-fan1", -1)); // already first

        state.ResetOrder();
        Assert.Equal(["s-fan1", "s-fan2", "s-fan3"], SensorExplorerBuilder.GroupOrder(Build(state), group));
    }

    [Fact]
    public void Preferences_use_stable_ids_regardless_of_snapshot_order()
    {
        var state = new SensorExplorerState();
        state.SetHidden("c-load", true);
        state.SetFavorite("s-fan2", true);
        var reversed = Snapshots.Of(DateTimeOffset.Now, Snapshot().Devices.Reverse(), Snapshot().Sensors.Reverse().ToArray());
        var nodes = SensorExplorerBuilder.Build(reversed, state);
        Assert.DoesNotContain(nodes, n => n.Sensor?.SensorId == "c-load");
        Assert.Equal(ExplorerKeys.FavoriteSensor("s-fan2"), nodes[1].Key);
    }

    [Fact]
    public void Missing_values_are_kept_and_marked_unavailable()
    {
        var node = Build(new SensorExplorerState()).Single(n => n.Sensor?.SensorId == "c-vcore");
        Assert.False(node.IsAvailable);
        Assert.Null(node.Sensor!.Value);
    }

    [Fact]
    public void Column_visibility_keeps_canonical_order()
    {
        var state = new SensorExplorerState();
        state.SetColumnVisible(ExplorerColumns.Provider, true);
        state.SetColumnVisible(ExplorerColumns.Value, false);
        state.SetColumnVisible(ExplorerColumns.Value, true);
        Assert.Equal([ExplorerColumns.Value, ExplorerColumns.Minimum, ExplorerColumns.Maximum, ExplorerColumns.Average, ExplorerColumns.Provider], state.VisibleColumns);
        Assert.False(state.IsColumnVisible(ExplorerColumns.Type));
    }
}

public sealed class ExplorerPreferenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nova-explorer-{Guid.NewGuid():N}.json");

    [Fact]
    public void Preferences_round_trip_through_json_store()
    {
        var store = new JsonPreferenceStore<SensorExplorerState>(_path, NullLogger.Instance);
        var state = new SensorExplorerState { DisplayMode = ExplorerDisplayMode.Detailed, RefreshIntervalMs = 2000, ShowHidden = true, SortColumn = SensorSortColumn.Maximum, SortDescending = true };
        state.SetExpanded(ExplorerKeys.Device("lhm:/amdcpu/0"), false);
        state.SetHidden("lhm:/amdcpu/0/voltage/3", true);
        state.SetFavorite("lhm:/gpu-nvidia/0/temperature/0", true);
        state.SensorOrder["grp:x|Fans"] = ["b", "a"];
        state.SetColumnVisible(ExplorerColumns.Quality, true);
        store.Save(state);

        var loaded = new JsonPreferenceStore<SensorExplorerState>(_path, NullLogger.Instance).Load().Sanitize();
        Assert.Equal(ExplorerDisplayMode.Detailed, loaded.DisplayMode);
        Assert.Equal(2000, loaded.RefreshIntervalMs);
        Assert.True(loaded.ShowHidden);
        Assert.Equal(SensorSortColumn.Maximum, loaded.SortColumn);
        Assert.True(loaded.SortDescending);
        Assert.False(loaded.IsExpanded(ExplorerKeys.Device("lhm:/amdcpu/0")));
        Assert.True(loaded.IsHidden("lhm:/amdcpu/0/voltage/3"));
        Assert.True(loaded.IsFavorite("lhm:/gpu-nvidia/0/temperature/0"));
        Assert.Equal(["b", "a"], loaded.SensorOrder["grp:x|Fans"]);
        Assert.True(loaded.IsColumnVisible(ExplorerColumns.Quality));
    }

    [Fact]
    public void Missing_or_corrupt_file_gives_defaults()
    {
        var store = new JsonPreferenceStore<SensorExplorerState>(_path, NullLogger.Instance);
        Assert.Equal(ExplorerDisplayMode.Compact, store.Load().DisplayMode);
        File.WriteAllText(_path, "{ nope");
        var state = store.Load().Sanitize();
        Assert.Equal(1000, state.RefreshIntervalMs);
        Assert.Equal(ExplorerColumns.Defaults, state.VisibleColumns);
    }

    [Fact]
    public void Sanitize_rejects_invalid_values()
    {
        var s = new SensorExplorerState { RefreshIntervalMs = 7, VisibleColumns = ["Bogus", ExplorerColumns.Average, ExplorerColumns.Value] }.Sanitize();
        Assert.Equal(1000, s.RefreshIntervalMs);
        Assert.Equal([ExplorerColumns.Value, ExplorerColumns.Average], s.VisibleColumns);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

public class PerSensorStatisticsTests
{
    [Fact]
    public void Resetting_one_sensor_keeps_others()
    {
        var p = new FakeProvider("lhm").Set("a", "d", "A", SensorType.Power, 10).Set("b", "d", "B", SensorType.Power, 10);
        var svc = new SensorPollingService([p], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
        svc.PollOnce();
        p.Set("a", "d", "A", SensorType.Power, 30).Set("b", "d", "B", SensorType.Power, 30);
        svc.PollOnce();
        svc.ResetStatistics("a");
        p.Set("a", "d", "A", SensorType.Power, 20).Set("b", "d", "B", SensorType.Power, 20);
        var snap = svc.PollOnce();

        Assert.Equal((20d, 20d, 20d, 1L), (snap.Find("a")!.Minimum!.Value, snap.Find("a")!.Maximum!.Value, snap.Find("a")!.Average!.Value, snap.Find("a")!.SampleCount));
        Assert.Equal((10d, 30d, 20d, 3L), (snap.Find("b")!.Minimum!.Value, snap.Find("b")!.Maximum!.Value, snap.Find("b")!.Average!.Value, snap.Find("b")!.SampleCount));
    }

    [Fact]
    public void Flag_values_format_as_state()
    {
        Assert.Equal("Active", SensorUnit.Flag.FormatValue(1));
        Assert.Equal("—", SensorUnit.Flag.FormatValue(0));
        Assert.Equal("N/A", SensorUnit.Flag.FormatValue(null));
        Assert.Equal(SensorUnit.Flag, SensorUnitExtensions.DefaultUnitFor(SensorType.PerformanceLimit));
    }
}
