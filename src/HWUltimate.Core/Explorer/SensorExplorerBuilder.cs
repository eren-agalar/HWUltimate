using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Core.Explorer;

public enum ExplorerNodeKind { Category = 0, Device, Group, Sensor }

/// <summary>One row of the flattened explorer tree. Only visible (expanded, unfiltered) rows are produced.</summary>
public sealed record ExplorerNode(
    string Key,
    ExplorerNodeKind Kind,
    int Depth,
    string Title,
    HardwareCategory Category,
    string? DeviceId = null,
    SensorGroupKind? Group = null,
    string? GroupKey = null,
    SensorReading? Sensor = null,
    bool IsExpanded = true,
    int ChildCount = 0,
    bool IsHidden = false,
    bool IsFavorite = false,
    bool IsAvailable = true,
    bool IsPinned = false,
    HardwareDevice? Device = null);

public sealed record ExplorerFilter(
    string? Search = null,
    SensorGroupKind? Group = null,
    HardwareCategory? Category = null,
    string? DeviceId = null,
    string? Provider = null,
    SensorStatusFilter Status = SensorStatusFilter.All,
    bool IncludeMetadata = false)
{
    public static ExplorerFilter None { get; } = new();

    public bool IsActive => !string.IsNullOrWhiteSpace(Search) || Group is not null || Category is not null
                            || DeviceId is not null || Provider is not null || Status != SensorStatusFilter.All;

    public bool Accepts(SensorReading s, SensorExplorerState state) => Status switch
    {
        SensorStatusFilter.Reporting => s.Value.HasValue && s.Quality == DataQuality.Good,
        SensorStatusFilter.NoData => !s.Value.HasValue,
        SensorStatusFilter.Attention => s.Quality is DataQuality.Suspect or DataQuality.Stale,
        SensorStatusFilter.Hidden => state.IsHidden(s.SensorId),
        SensorStatusFilter.Pinned => state.IsPinned(s.SensorId),
        _ => true,
    };
}

/// <summary>
/// Builds the sensor tree Device → Sensor group → Sensor from a snapshot (the layout of classic hardware monitors):
/// devices in category order, Super I/O / EC chips folded into their motherboard, every core reading in its type group.
/// Applies user preferences (collapsed nodes, hidden sensors / devices / groups, favourites, custom order), the
/// secondary-sensor rules and filters. Pure; the UI rebuilds only when the structure changes.
/// </summary>
public static class SensorExplorerBuilder
{
    /// <summary>Display order of hardware categories.</summary>
    public static IReadOnlyList<HardwareCategory> CategoryOrder { get; } =
    [
        HardwareCategory.Cpu, HardwareCategory.Gpu, HardwareCategory.Memory, HardwareCategory.MemoryModule, HardwareCategory.Motherboard,
        HardwareCategory.SuperIo, HardwareCategory.EmbeddedController, HardwareCategory.Storage, HardwareCategory.Network,
        HardwareCategory.Cooler, HardwareCategory.PowerSupply, HardwareCategory.Battery, HardwareCategory.Other,
    ];

    /// <summary>Explorer category for a device. Super I/O and EC chips are shown under Motherboard.</summary>
    public static HardwareCategory ExplorerCategory(HardwareCategory c) => c switch
    {
        HardwareCategory.SuperIo or HardwareCategory.EmbeddedController => HardwareCategory.Motherboard,
        _ when CategoryOrder.Contains(c) => c,
        _ => HardwareCategory.Other,
    };

    private sealed record GroupEntry(SensorGroupKind Kind, string Key, List<SensorReading> Sensors, bool IsHidden);

    private sealed record DeviceEntry(HardwareDevice Device, List<GroupEntry> Groups, bool IsHidden, int SecondaryCount);

    /// <summary>Result details the view shows next to the tree.</summary>
    public sealed record BuildStats(int SecondaryHidden, int UserHidden);

    public static IReadOnlyList<ExplorerNode> Build(SensorSnapshot snapshot, SensorExplorerState state, ExplorerFilter? filter = null) =>
        Build(snapshot, state, filter, out _);

    public static IReadOnlyList<ExplorerNode> Build(SensorSnapshot snapshot, SensorExplorerState state, ExplorerFilter? filter, out BuildStats stats)
    {
        filter ??= ExplorerFilter.None;
        var nodes = new List<ExplorerNode>(snapshot.Sensors.Count + 64);
        var devices = Organize(snapshot, state, filter, out stats);
        var forceExpand = filter.IsActive;
        var showMetadata = filter.IncludeMetadata;

        // Favourites (flat, user-ordered) — shown only when no filter narrows the view.
        if (!forceExpand && state.FavoriteSensors.Count > 0)
        {
            var favs = state.FavoriteSensors.Select(snapshot.Find).OfType<SensorReading>().Where(s => showMetadata || s.IsMeasurement).ToList();
            if (favs.Count > 0)
            {
                var expanded = state.IsExpanded(ExplorerKeys.Favorites);
                nodes.Add(new ExplorerNode(ExplorerKeys.Favorites, ExplorerNodeKind.Category, 0, "Favorites", HardwareCategory.Other,
                    IsExpanded: expanded, ChildCount: favs.Count, IsFavorite: true));
                if (expanded)
                {
                    foreach (var s in favs)
                    {
                        nodes.Add(SensorNode(ExplorerKeys.FavoriteSensor(s.SensorId), s, 1, ExplorerKeys.Favorites, state, null));
                    }
                }
            }
        }

        var ordered = devices
            .Select((d, i) => (d, i))
            .OrderBy(x => CategoryRank(x.d.Device.Category))
            .ThenBy(x => DeviceRank(state.SortColumn) is { } rankType
                ? -x.d.Groups.SelectMany(g => g.Sensors).Where(s => s.Type == rankType && s.Value.HasValue).Select(s => s.Value!.Value).DefaultIfEmpty(double.MinValue).Max()
                : 0)
            .ThenBy(x => x.i)
            .Select(x => x.d);

        foreach (var entry in ordered)
        {
            var dev = entry.Device;
            var devKey = ExplorerKeys.Device(dev.DeviceId);
            var devExpanded = forceExpand || state.IsExpanded(devKey);
            var sensorCount = entry.Groups.Sum(g => g.Sensors.Count);
            var available = entry.Groups.Any(g => g.Sensors.Any(s => s.Value.HasValue));
            nodes.Add(new ExplorerNode(devKey, ExplorerNodeKind.Device, 0, dev.Name, dev.Category, dev.DeviceId,
                IsExpanded: devExpanded, ChildCount: sensorCount, IsAvailable: available, Device: dev, IsHidden: entry.IsHidden));
            if (!devExpanded)
            {
                continue;
            }

            foreach (var group in entry.Groups)
            {
                var grpExpanded = forceExpand || state.IsExpanded(group.Key);
                nodes.Add(new ExplorerNode(group.Key, ExplorerNodeKind.Group, 1, group.Kind.ToString(), dev.Category, dev.DeviceId, group.Kind, group.Key,
                    IsExpanded: grpExpanded, ChildCount: group.Sensors.Count, IsHidden: group.IsHidden));
                if (!grpExpanded)
                {
                    continue;
                }

                foreach (var s in group.Sensors)
                {
                    nodes.Add(SensorNode(ExplorerKeys.Sensor(s.SensorId), s, 2, group.Key, state, group.Kind));
                }
            }
        }

        return nodes;
    }

    private static int CategoryRank(HardwareCategory c)
    {
        var i = 0;
        var target = ExplorerCategory(c);
        foreach (var x in CategoryOrder)
        {
            if (x == target) return i;
            i++;
        }

        return i;
    }

    private static ExplorerNode SensorNode(string key, SensorReading s, int depth, string groupKey, SensorExplorerState state, SensorGroupKind? kind) =>
        new(key, ExplorerNodeKind.Sensor, depth, s.Name, HardwareCategory.Other, s.DeviceId, kind, groupKey, s,
            IsHidden: state.IsHidden(s.SensorId), IsFavorite: state.IsFavorite(s.SensorId), IsAvailable: s.Value.HasValue, IsPinned: state.IsPinned(s.SensorId));

    private static SensorType? DeviceRank(SensorSortColumn c) => c switch
    {
        SensorSortColumn.Temperature => SensorType.Temperature,
        SensorSortColumn.Power => SensorType.Power,
        SensorSortColumn.Utilization => SensorType.Utilization,
        _ => null,
    };

    /// <summary>
    /// The device a sensor is shown under: Super I/O and EC chips fold into their parent motherboard when it is present,
    /// so one board lists all its temperatures, voltages and fans together.
    /// </summary>
    public static Dictionary<string, HardwareDevice> DisplayDevices(SensorSnapshot snapshot)
    {
        var byId = snapshot.Devices.GroupBy(d => d.DeviceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new Dictionary<string, HardwareDevice>(StringComparer.Ordinal);
        foreach (var d in byId.Values)
        {
            var target = d;
            for (var guard = 0; guard < 4 && target.Category is HardwareCategory.SuperIo or HardwareCategory.EmbeddedController
                                              && target.ParentDeviceId is { } p && byId.TryGetValue(p, out var parent); guard++)
            {
                target = parent;
            }

            result[d.DeviceId] = target;
        }

        return result;
    }

    private static List<DeviceEntry> Organize(SensorSnapshot snapshot, SensorExplorerState state, ExplorerFilter filter, out BuildStats stats)
    {
        var search = filter.Search?.Trim();
        var hasSearch = !string.IsNullOrEmpty(search);
        var display = DisplayDevices(snapshot);
        var sensorsByDisplay = snapshot.Sensors
            .GroupBy(s => display.TryGetValue(s.DeviceId, out var d) ? d.DeviceId : s.DeviceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<DeviceEntry>();
        int secondaryTotal = 0, userHidden = 0;
        var showHidden = state.ShowHidden || filter.Status == SensorStatusFilter.Hidden;
        var ownCategory = snapshot.Devices.DistinctBy(d => d.DeviceId).ToDictionary(d => d.DeviceId, d => d.Category, StringComparer.Ordinal);
        var ownName = snapshot.Devices.DistinctBy(d => d.DeviceId).ToDictionary(d => d.DeviceId, d => d.Name, StringComparer.Ordinal);

        foreach (var device in snapshot.Devices.DistinctBy(d => d.DeviceId))
        {
            if (display.TryGetValue(device.DeviceId, out var shownAs) && shownAs.DeviceId != device.DeviceId) continue; // folded into its parent
            if (filter.Category is { } fc && ExplorerCategory(device.Category) != ExplorerCategory(fc)) continue;
            if (filter.DeviceId is { } fd && device.DeviceId != fd) continue;
            if (!sensorsByDisplay.TryGetValue(device.DeviceId, out var sensors)) continue;

            // Virtual / disconnected devices (e.g. Hyper-V or VPN network adapters) only with "Show all", or when picked explicitly.
            if (device.Relevance != DeviceRelevance.Primary && !state.ShowAllSensors && filter.DeviceId is null)
            {
                secondaryTotal += sensors.Count;
                continue;
            }

            var deviceHidden = state.IsNodeHidden(ExplorerKeys.Device(device.DeviceId));
            if (deviceHidden && !showHidden)
            {
                userHidden += sensors.Count;
                continue;
            }

            var deviceMatches = hasSearch && device.Name.Contains(search!, StringComparison.CurrentCultureIgnoreCase);
            var groups = new List<GroupEntry>();
            var secondary = 0;
            foreach (var g in sensors.GroupBy(s => SensorClassifier.Classify(s, device.Category)).OrderBy(g => g.Key))
            {
                if (filter.Group is { } fg && g.Key != fg) continue;
                if (g.Key == SensorGroupKind.Metadata && !filter.IncludeMetadata) continue;

                var groupKey = ExplorerKeys.Group(device.DeviceId, g.Key);
                var groupHidden = state.IsNodeHidden(groupKey);
                if (groupHidden && !showHidden)
                {
                    userHidden += g.Count();
                    continue;
                }

                var list = new List<SensorReading>();
                foreach (var s in g)
                {
                    if (!showHidden && state.IsHidden(s.SensorId)) { userHidden++; continue; }
                    if (filter.Provider is not null && s.ProviderName != filter.Provider) continue;
                    if (!filter.Accepts(s, state)) continue;
                    if (hasSearch && !deviceMatches && !Matches(s, search!)
                        && !(ownName.TryGetValue(s.DeviceId, out var chip) && chip.Contains(search!, StringComparison.CurrentCultureIgnoreCase))) continue;
                    // Secondary sensors: shown with "Show all", when searched for, or when the user asked for them by status / pin / favourite.
                    if (!state.ShowAllSensors && !hasSearch && !state.IsPinned(s.SensorId) && !state.IsFavorite(s.SensorId)
                        && filter.Status is SensorStatusFilter.All or SensorStatusFilter.Reporting
                        && SensorNoiseRules.IsSecondary(s, display.TryGetValue(s.DeviceId, out _) && ownCategory.TryGetValue(s.DeviceId, out var own) ? own : device.Category, g.Key))
                    {
                        secondary++;
                        continue;
                    }

                    list.Add(s);
                }

                if (list.Count == 0) continue;
                groups.Add(new GroupEntry(g.Key, groupKey, Sort(list, groupKey, state), groupHidden));
            }

            secondaryTotal += secondary;
            if (groups.Count > 0)
            {
                result.Add(new DeviceEntry(device, groups, deviceHidden, secondary));
            }
        }

        stats = new BuildStats(secondaryTotal, userHidden);
        return result;
    }

    private static bool Matches(SensorReading s, string search) =>
        s.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || s.Type.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
        || s.SensorId.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>Default order = user's custom order first, then provider index order.</summary>
    public static List<SensorReading> Sort(List<SensorReading> sensors, string groupKey, SensorExplorerState state)
    {
        List<SensorReading> ordered;
        if (state.SensorOrder.TryGetValue(groupKey, out var custom) && custom.Count > 0)
        {
            var rank = custom.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
            ordered = sensors.Select((s, i) => (s, i))
                .OrderBy(x => rank.TryGetValue(x.s.SensorId, out var r) ? r : int.MaxValue)
                .ThenBy(x => x.i)
                .Select(x => x.s).ToList();
        }
        else
        {
            ordered = sensors;
        }

        return PinnedFirst(SortCore(ordered, state), state);
    }

    private static List<SensorReading> PinnedFirst(List<SensorReading> sorted, SensorExplorerState state) =>
        state.PinnedSensors.Count == 0 ? sorted : sorted.Where(s => state.IsPinned(s.SensorId)).Concat(sorted.Where(s => !state.IsPinned(s.SensorId))).ToList();

    private static List<SensorReading> SortCore(List<SensorReading> ordered, SensorExplorerState state)
    {
        if (state.SortColumn == SensorSortColumn.Default)
        {
            return state.SortDescending ? Enumerable.Reverse(ordered).ToList() : ordered;
        }

        if (DeviceRank(state.SortColumn) is { } type)
        {
            // Matching sensors first, highest value first (or lowest when reversed); others keep their default order.
            var matching = ordered.Where(s => s.Type == type && s.Value.HasValue);
            var sortedMatching = state.SortDescending ? matching.OrderBy(s => s.Value) : matching.OrderByDescending(s => s.Value);
            return sortedMatching.Concat(ordered.Where(s => !(s.Type == type && s.Value.HasValue))).ToList();
        }

        Func<SensorReading, double?> numeric = state.SortColumn switch
        {
            SensorSortColumn.Value => s => s.Value,
            SensorSortColumn.Minimum => s => s.Minimum,
            SensorSortColumn.Maximum => s => s.Maximum,
            SensorSortColumn.Average => s => s.Average,
            _ => _ => null,
        };

        if (state.SortColumn == SensorSortColumn.Name)
        {
            var byName = ordered.OrderBy(s => s.Name, NaturalComparer.Instance);
            return (state.SortDescending ? byName.Reverse() : byName).ToList();
        }

        // Sensors without a value always sort last, whatever the direction.
        var withValue = ordered.Where(s => numeric(s).HasValue);
        var sorted = state.SortDescending ? withValue.OrderByDescending(s => numeric(s)) : withValue.OrderBy(s => numeric(s));
        return sorted.Concat(ordered.Where(s => !numeric(s).HasValue)).ToList();
    }

    /// <summary>Keys of every container node for the snapshot (used by "collapse all").</summary>
    public static IEnumerable<string> ContainerKeys(SensorSnapshot snapshot)
    {
        yield return ExplorerKeys.Favorites;
        var display = DisplayDevices(snapshot);
        foreach (var d in display.Values.DistinctBy(d => d.DeviceId))
        {
            yield return ExplorerKeys.Device(d.DeviceId);
        }

        foreach (var key in snapshot.Sensors
                     .Select(s => display.TryGetValue(s.DeviceId, out var d) ? ExplorerKeys.Group(d.DeviceId, SensorClassifier.Classify(s, d.Category)) : null)
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            yield return key;
        }
    }

    /// <summary>Ordered sensor ids currently displayed in a group (input for <see cref="SensorExplorerState.Move"/>).</summary>
    public static IReadOnlyList<string> GroupOrder(IReadOnlyList<ExplorerNode> nodes, string groupKey) =>
        nodes.Where(n => n.Kind == ExplorerNodeKind.Sensor && n.GroupKey == groupKey).Select(n => n.Sensor!.SensorId).ToList();
}

/// <summary>Orders "Core #2" before "Core #10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = a.CompareTo(b, StringComparison.Ordinal);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
