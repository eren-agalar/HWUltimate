using HWUltimate.Domain.Sensors;

namespace HWUltimate.Core.Explorer;

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
    public static IReadOnlyList<string> Defaults { get; } = [Value, Minimum, Maximum];
}

/// <summary>
/// Persistent user preferences for the sensor explorer. Everything is keyed by stable identifiers
/// (provider sensor ids, device ids and derived node keys), never by list indexes, so preferences
/// survive restarts, hardware re-ordering and new sensors appearing.
/// </summary>
public sealed class SensorExplorerState
{
    public int Version { get; set; } = 2;

    /// <summary>Node keys (see <see cref="ExplorerKeys"/>) the user collapsed. Everything else is expanded.</summary>
    public HashSet<string> CollapsedNodes { get; set; } = new(StringComparer.Ordinal);

    public HashSet<string> HiddenSensors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whole devices (<c>dev:</c> keys) or sensor groups (<c>grp:</c> keys) the user turned off.</summary>
    public HashSet<string> HiddenNodes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// "Device name | type | sensor name" for every sensor the user hid, favourited or pinned. Some providers number
    /// devices by enumeration order (e.g. drives), so an id can change between boots; the fingerprint lets
    /// <see cref="Rebind"/> move the preference to the sensor's new id instead of silently losing it.
    /// </summary>
    public Dictionary<string, string> Fingerprints { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Device name (and group) for every hidden device / group key, for the same reason.</summary>
    public Dictionary<string, string> NodeFingerprints { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Show sensors that are hidden by default as unused or redundant (see <see cref="SensorNoiseRules"/>).</summary>
    public bool ShowAllSensors { get; set; }

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

    /// <summary>Hides a sensor and, with it, the same measurement from lower-priority providers.</summary>
    public void SetHidden(string sensorId, bool hidden, string? fingerprint, IEnumerable<string>? duplicateIds = null)
    {
        foreach (var id in (duplicateIds ?? []).Prepend(sensorId))
        {
            SetHidden(id, hidden);
            if (hidden && fingerprint is not null) Fingerprints[id] = fingerprint;
        }
    }

    public void UnhideAll()
    {
        HiddenSensors.Clear();
        HiddenNodes.Clear();
        NodeFingerprints.Clear();
    }

    public bool IsNodeHidden(string nodeKey) => HiddenNodes.Contains(nodeKey);

    public void SetNodeHidden(string nodeKey, bool hidden, string? fingerprint)
    {
        if (hidden)
        {
            HiddenNodes.Add(nodeKey);
            if (fingerprint is not null) NodeFingerprints[nodeKey] = fingerprint;
        }
        else
        {
            HiddenNodes.Remove(nodeKey);
            NodeFingerprints.Remove(nodeKey);
        }
    }

    public static string FingerprintOf(SensorReading s, string deviceName) => $"{deviceName}|{s.Type}|{s.Name}";

    /// <summary>
    /// Moves preferences to the sensor / device that now carries the saved fingerprint, when the id they were saved under
    /// is absent or now belongs to something else (e.g. two drives that swapped numbers at boot). Ambiguous fingerprints
    /// (two identical devices) are left alone. Returns true when anything moved.
    /// </summary>
    public bool Rebind(SensorSnapshot snapshot)
    {
        if (snapshot.Sensors.Count == 0) return false;
        var deviceNames = snapshot.Devices.GroupBy(d => d.DeviceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        string PrintOf(SensorReading s) => FingerprintOf(s, deviceNames.GetValueOrDefault(s.DeviceId, s.DeviceId));
        var byPrint = snapshot.Sensors.GroupBy(PrintOf, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(s => s.SensorId).ToList(), StringComparer.Ordinal);

        string Target(string id)
        {
            if (!Fingerprints.TryGetValue(id, out var fp)) return id;
            if (snapshot.Find(id) is { } current && PrintOf(current) == fp) return id;
            return byPrint.TryGetValue(fp, out var candidates) && candidates.Count == 1 ? candidates[0] : id;
        }

        var changed = false;
        var prints = new Dictionary<string, string>(Fingerprints, StringComparer.Ordinal);
        void Carry(string from, string to)
        {
            if (from == to) return;
            changed = true;
            if (prints.TryGetValue(from, out var fp)) Fingerprints[to] = fp;
        }

        // Targets are computed from the saved state first, then applied, so swapped ids exchange cleanly.
        var hidden = HiddenSensors.Select(id => (id, to: Target(id))).ToList();
        var pinned = PinnedSensors.Select(id => (id, to: Target(id))).ToList();
        var favorites = FavoriteSensors.Select(id => (id, to: Target(id))).ToList();
        foreach (var (from, to) in hidden.Concat(pinned).Concat(favorites)) Carry(from, to);
        HiddenSensors = new HashSet<string>(hidden.Select(x => x.to), StringComparer.Ordinal);
        PinnedSensors = new HashSet<string>(pinned.Select(x => x.to), StringComparer.Ordinal);
        FavoriteSensors = favorites.Select(x => x.to).Distinct(StringComparer.Ordinal).ToList();

        // Hidden devices / groups ("dev:{id}", "grp:{id}|{group}") keyed by device name.
        var nodeMoves = new List<(string From, string To, string Name)>();
        foreach (var key in HiddenNodes)
        {
            if (!NodeFingerprints.TryGetValue(key, out var name) || ExplorerKeys.DeviceIdOf(key) is not { } deviceId) continue;
            if (deviceNames.TryGetValue(deviceId, out var currentName) && currentName == name) continue;
            var matches = deviceNames.Where(d => d.Value == name).Select(d => d.Key).ToList();
            if (matches.Count == 1) nodeMoves.Add((key, ExplorerKeys.WithDevice(key, matches[0]), name));
        }

        foreach (var (from, _, _) in nodeMoves)
        {
            HiddenNodes.Remove(from);
            NodeFingerprints.Remove(from);
        }

        foreach (var (_, to, name) in nodeMoves)
        {
            HiddenNodes.Add(to);
            NodeFingerprints[to] = name;
            changed = true;
        }

        // Drop fingerprints nothing refers to any more.
        foreach (var id in Fingerprints.Keys.ToList())
        {
            if (!HiddenSensors.Contains(id) && !PinnedSensors.Contains(id) && !FavoriteSensors.Contains(id)) Fingerprints.Remove(id);
        }

        return changed;
    }

    // ------------------------------------------------------------------ favourites
    public bool IsFavorite(string sensorId) => FavoriteSensors.Contains(sensorId, StringComparer.Ordinal);

    public void SetFavorite(string sensorId, bool favorite)
    {
        FavoriteSensors.RemoveAll(s => s == sensorId);
        if (favorite) FavoriteSensors.Add(sensorId);
    }

    public void SetFavorite(string sensorId, bool favorite, string? fingerprint)
    {
        SetFavorite(sensorId, favorite);
        if (favorite && fingerprint is not null) Fingerprints[sensorId] = fingerprint;
    }

    public void SetPinned(string sensorId, bool pinned, string? fingerprint)
    {
        SetPinned(sensorId, pinned);
        if (pinned && fingerprint is not null) Fingerprints[sensorId] = fingerprint;
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
        HiddenNodes = new HashSet<string>(HiddenNodes ?? [], StringComparer.Ordinal);
        Fingerprints = new Dictionary<string, string>(Fingerprints ?? [], StringComparer.Ordinal);
        NodeFingerprints = new Dictionary<string, string>(NodeFingerprints ?? [], StringComparer.Ordinal);
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

    /// <summary>The device id inside a <c>dev:</c> or <c>grp:</c> key; null for other keys.</summary>
    public static string? DeviceIdOf(string key)
    {
        if (key.StartsWith("dev:", StringComparison.Ordinal)) return key[4..];
        if (!key.StartsWith("grp:", StringComparison.Ordinal)) return null;
        var bar = key.LastIndexOf('|');
        return bar > 4 ? key[4..bar] : null;
    }

    /// <summary>The same <c>dev:</c> / <c>grp:</c> key for another device id.</summary>
    public static string WithDevice(string key, string deviceId) =>
        key.StartsWith("dev:", StringComparison.Ordinal) ? Device(deviceId) : $"grp:{deviceId}{key[key.LastIndexOf('|')..]}";
}
