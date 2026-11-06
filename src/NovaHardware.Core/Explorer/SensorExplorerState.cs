namespace NovaHardware.Core.Explorer;

public enum ExplorerDisplayMode
{
    /// <summary>Dense rows, five core columns. The default.</summary>
    Compact = 0,
    /// <summary>Taller rows with sensor id, provider, kind and sample count; technical metadata group visible.</summary>
    Advanced = 1,
    /// <summary>v0.1 name of <see cref="Advanced"/>, kept so saved preferences still load.</summary>
    Detailed = Advanced,
}

public enum SensorSortColumn
{
    Default = 0,
    Name,
    Value,
    Minimum,
    Maximum,
    Average,
    /// <summary>Hottest first: temperature sensors by value, then the rest in default order; devices ordered by their hottest sensor.</summary>
    Temperature,
    /// <summary>Highest power first.</summary>
    Power,
    /// <summary>Highest utilisation first.</summary>
    Utilization,
}

public enum SensorStatusFilter { All = 0, Reporting, NoData, Attention, Hidden, Pinned }

public static class ExplorerColumns
{
    public const string Value = "Value";
    public const string Minimum = "Minimum";
    public const string Maximum = "Maximum";
    public const string Average = "Average";
    public const string Type = "Type";
    public const string Provider = "Provider";
    public const string Quality = "Quality";
    public const string SensorId = "SensorId";
    public const string Samples = "Samples";

    public static IReadOnlyList<string> All { get; } = [Value, Minimum, Maximum, Average, Type, Provider, Quality, SensorId, Samples];
    public static IReadOnlyList<string> Defaults { get; } = [Value, Minimum, Maximum, Average];
}

/// <summary>
/// Persistent user preferences for the sensor explorer. Everything is keyed by stable identifiers
/// (provider sensor ids, device ids and derived node keys), never by list indexes, so preferences
/// survive restarts, hardware re-ordering and new sensors appearing.
/// </summary>
public sealed class SensorExplorerState
{
    public int Version { get; set; } = 1;

    /// <summary>Node keys (see <see cref="ExplorerKeys"/>) the user collapsed. Everything else is expanded.</summary>
    public HashSet<string> CollapsedNodes { get; set; } = new(StringComparer.Ordinal);

    public HashSet<string> HiddenSensors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Favourite sensor ids, in display order.</summary>
    public List<string> FavoriteSensors { get; set; } = [];

    /// <summary>Pinned sensors always appear first within their group, whatever the sort order.</summary>
    public HashSet<string> PinnedSensors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Custom sensor order per group key. Sensors not listed follow in provider order.</summary>
    public Dictionary<string, List<string>> SensorOrder { get; set; } = new(StringComparer.Ordinal);

    public ExplorerDisplayMode DisplayMode { get; set; } = ExplorerDisplayMode.Compact;

    public List<string> VisibleColumns { get; set; } = [.. ExplorerColumns.Defaults];

    public int RefreshIntervalMs { get; set; } = 1000;

    public bool ShowHidden { get; set; }

    public SensorSortColumn SortColumn { get; set; } = SensorSortColumn.Default;

    public bool SortDescending { get; set; }

    public static IReadOnlyList<int> RefreshIntervals { get; } = [250, 500, 1000, 2000, 5000];

    // ------------------------------------------------------------------ expand / collapse
    public bool IsExpanded(string nodeKey) => !CollapsedNodes.Contains(nodeKey);

    public void SetExpanded(string nodeKey, bool expanded)
    {
        if (expanded) CollapsedNodes.Remove(nodeKey);
        else CollapsedNodes.Add(nodeKey);
    }

    public void Toggle(string nodeKey) => SetExpanded(nodeKey, !IsExpanded(nodeKey));

    public void ExpandAll() => CollapsedNodes.Clear();

    public void CollapseAll(IEnumerable<string> containerKeys)
    {
        foreach (var key in containerKeys) CollapsedNodes.Add(key);
    }

    // ------------------------------------------------------------------ visibility
    public bool IsHidden(string sensorId) => HiddenSensors.Contains(sensorId);

    public void SetHidden(string sensorId, bool hidden)
    {
        if (hidden) HiddenSensors.Add(sensorId);
        else HiddenSensors.Remove(sensorId);
    }

    public void UnhideAll() => HiddenSensors.Clear();

    // ------------------------------------------------------------------ favourites
    public bool IsFavorite(string sensorId) => FavoriteSensors.Contains(sensorId, StringComparer.Ordinal);

    public void SetFavorite(string sensorId, bool favorite)
    {
        FavoriteSensors.RemoveAll(s => s == sensorId);
        if (favorite) FavoriteSensors.Add(sensorId);
    }

    public void ToggleFavorite(string sensorId) => SetFavorite(sensorId, !IsFavorite(sensorId));

    // ------------------------------------------------------------------ pins
    public bool IsPinned(string sensorId) => PinnedSensors.Contains(sensorId);

    public void SetPinned(string sensorId, bool pinned)
    {
        if (pinned) PinnedSensors.Add(sensorId);
        else PinnedSensors.Remove(sensorId);
    }

    public void TogglePinned(string sensorId) => SetPinned(sensorId, !IsPinned(sensorId));

    // ------------------------------------------------------------------ columns
    public bool IsColumnVisible(string column) => VisibleColumns.Contains(column, StringComparer.Ordinal);

    public void SetColumnVisible(string column, bool visible)
    {
        VisibleColumns.RemoveAll(c => c == column);
        if (visible)
        {
            // Keep canonical column order.
            VisibleColumns.Add(column);
            VisibleColumns = ExplorerColumns.All.Where(VisibleColumns.Contains).ToList();
        }
    }

    // ------------------------------------------------------------------ ordering
    /// <summary>
    /// Moves a sensor one step up (<paramref name="delta"/> = -1) or down (+1) within its group.
    /// <paramref name="currentOrder"/> is the group's current displayed order.
    /// For the favourites list pass <see cref="ExplorerKeys.Favorites"/> as the group key.
    /// </summary>
    public bool Move(string groupKey, IReadOnlyList<string> currentOrder, string sensorId, int delta)
    {
        var list = groupKey == ExplorerKeys.Favorites ? FavoriteSensors : [.. currentOrder];
        var index = list.IndexOf(sensorId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return false;
        }

        (list[index], list[target]) = (list[target], list[index]);
        if (groupKey != ExplorerKeys.Favorites)
        {
            SensorOrder[groupKey] = list;
        }

        return true;
    }

    public void ResetOrder() => SensorOrder.Clear();

    /// <summary>Normalises a deserialised instance (null collections, comparer, invalid values).</summary>
    public SensorExplorerState Sanitize()
    {
        CollapsedNodes = new HashSet<string>(CollapsedNodes ?? [], StringComparer.Ordinal);
        HiddenSensors = new HashSet<string>(HiddenSensors ?? [], StringComparer.Ordinal);
        PinnedSensors = new HashSet<string>(PinnedSensors ?? [], StringComparer.Ordinal);
        FavoriteSensors = (FavoriteSensors ?? []).Distinct(StringComparer.Ordinal).ToList();
        SensorOrder = new Dictionary<string, List<string>>(SensorOrder ?? [], StringComparer.Ordinal);
        VisibleColumns = ExplorerColumns.All.Where((VisibleColumns ?? [.. ExplorerColumns.Defaults]).Contains).ToList();
        if (!RefreshIntervals.Contains(RefreshIntervalMs)) RefreshIntervalMs = 1000;
        if (!Enum.IsDefined(DisplayMode)) DisplayMode = ExplorerDisplayMode.Compact;
        if (!Enum.IsDefined(SortColumn)) SortColumn = SensorSortColumn.Default;
        return this;
    }
}

public static class ExplorerKeys
{
    public const string Favorites = "favorites";

    public static string Category(Domain.Hardware.HardwareCategory c) => $"cat:{c}";
    public static string Device(string deviceId) => $"dev:{deviceId}";
    public static string Group(string deviceId, SensorGroupKind g) => $"grp:{deviceId}|{g}";
    public static string Sensor(string sensorId) => $"sen:{sensorId}";
    public static string FavoriteSensor(string sensorId) => $"fav:{sensorId}";
}
