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
/// Builds the explorer hierarchy Category → Device → Sensor group → Sensor from a snapshot, applying
/// user preferences (collapsed nodes, hidden sensors, favourites, custom order) and filters.
/// Pure and allocation-light; the UI rebuilds only when the structure changes.
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

    private sealed record DeviceEntry(HardwareDevice Device, List<(SensorGroupKind Kind, List<SensorReading> Sensors)> Groups);

    public static IReadOnlyList<ExplorerNode> Build(SensorSnapshot snapshot, SensorExplorerState state, ExplorerFilter? filter = null)
    {
        filter ??= ExplorerFilter.None;
        var nodes = new List<ExplorerNode>(snapshot.Sensors.Count + 64);
        var devices = Organize(snapshot, state, filter);
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

        foreach (var category in CategoryOrder)
        {
            var inCategory = devices.Where(d => ExplorerCategory(d.Device.Category) == category).ToList();
            if (DeviceRank(state.SortColumn) is { } rankType)
            {
                // "Sort by temperature / power / utilisation" also orders devices by their highest such reading.
                inCategory = inCategory.OrderByDescending(d => d.Groups.SelectMany(g => g.Sensors).Where(s => s.Type == rankType && s.Value.HasValue)
                    .Select(s => s.Value!.Value).DefaultIfEmpty(double.MinValue).Max()).ToList();
            }
            if (inCategory.Count == 0)
            {
                continue;
            }

            var catKey = ExplorerKeys.Category(category);
            var catExpanded = forceExpand || state.IsExpanded(catKey);
            nodes.Add(new ExplorerNode(catKey, ExplorerNodeKind.Category, 0, category.ToString(), category,
                IsExpanded: catExpanded, ChildCount: inCategory.Count));
            if (!catExpanded)
            {
                continue;
            }

            foreach (var entry in inCategory)
            {
                var dev = entry.Device;
                var devKey = ExplorerKeys.Device(dev.DeviceId);
                var devExpanded = forceExpand || state.IsExpanded(devKey);
                var sensorCount = entry.Groups.Sum(g => g.Sensors.Count);
                var available = entry.Groups.Any(g => g.Sensors.Any(s => s.Value.HasValue));
                nodes.Add(new ExplorerNode(devKey, ExplorerNodeKind.Device, 1, dev.Name, dev.Category, dev.DeviceId,
                    IsExpanded: devExpanded, ChildCount: sensorCount, IsAvailable: available, Device: dev));
                if (!devExpanded)
                {
                    continue;
                }

                foreach (var (kind, sensors) in entry.Groups)
                {
                    var grpKey = ExplorerKeys.Group(dev.DeviceId, kind);
                    var grpExpanded = forceExpand || state.IsExpanded(grpKey);
                    nodes.Add(new ExplorerNode(grpKey, ExplorerNodeKind.Group, 2, kind.ToString(), dev.Category, dev.DeviceId, kind, grpKey,
                        IsExpanded: grpExpanded, ChildCount: sensors.Count));
                    if (!grpExpanded)
                    {
                        continue;
                    }

                    foreach (var s in sensors)
                    {
                        nodes.Add(SensorNode(ExplorerKeys.Sensor(s.SensorId), s, 3, grpKey, state, kind));
                    }
                }
            }
        }

        return nodes;
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

    private static List<DeviceEntry> Organize(SensorSnapshot snapshot, SensorExplorerState state, ExplorerFilter filter)
    {
        var search = filter.Search?.Trim();
        var bySensorDevice = snapshot.Sensors.GroupBy(s => s.DeviceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<DeviceEntry>();

        foreach (var device in snapshot.Devices.DistinctBy(d => d.DeviceId))
        {
            if (filter.Category is { } fc && ExplorerCategory(device.Category) != ExplorerCategory(fc))
            {
                continue;
            }

            if (filter.DeviceId is { } fd && device.DeviceId != fd)
            {
                continue;
            }

            if (!bySensorDevice.TryGetValue(device.DeviceId, out var sensors))
            {
                continue;
            }

            var deviceMatches = !string.IsNullOrEmpty(search) && device.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase);
            var groups = new List<(SensorGroupKind, List<SensorReading>)>();
            foreach (var g in sensors.GroupBy(s => SensorClassifier.Classify(s, device.Category)).OrderBy(g => g.Key))
            {
                if (filter.Group is { } fg && g.Key != fg)
                {
                    continue;
                }

                if (g.Key == SensorGroupKind.Metadata && !filter.IncludeMetadata)
                {
                    continue;
                }

                var groupKey = ExplorerKeys.Group(device.DeviceId, g.Key);
                var list = g.Where(s => state.ShowHidden || filter.Status == SensorStatusFilter.Hidden || !state.IsHidden(s.SensorId))
                            .Where(s => filter.Provider is null || s.ProviderName == filter.Provider)
                            .Where(s => filter.Accepts(s, state))
                            .Where(s => string.IsNullOrEmpty(search) || deviceMatches || Matches(s, search))
                            .ToList();
                if (list.Count == 0)
                {
                    continue;
                }

                groups.Add((g.Key, Sort(list, groupKey, state)));
            }

            if (groups.Count > 0)
            {
                result.Add(new DeviceEntry(device, groups));
            }
        }

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
        var cats = new HashSet<HardwareCategory>();
        foreach (var d in snapshot.Devices)
        {
            if (cats.Add(ExplorerCategory(d.Category))) yield return ExplorerKeys.Category(ExplorerCategory(d.Category));
            yield return ExplorerKeys.Device(d.DeviceId);
        }

        var devices = snapshot.Devices.ToDictionary(d => d.DeviceId, d => d.Category, StringComparer.Ordinal);
        foreach (var key in snapshot.Sensors
                     .Select(s => ExplorerKeys.Group(s.DeviceId, SensorClassifier.Classify(s, devices.GetValueOrDefault(s.DeviceId))))
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
