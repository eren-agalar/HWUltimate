using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HWUltimate.App.Services;
using HWUltimate.Core.Abstractions;
using HWUltimate.Core.Explorer;
using HWUltimate.Core.Settings;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;
using HWUltimate.UI.Controls;
using HWUltimate.UI.Localization;

namespace HWUltimate.App.ViewModels;

/// <summary>Column widths and row height shared by the header and every row.</summary>
public sealed partial class ExplorerLayout : ObservableObject
{
    [ObservableProperty] private GridLength _value;
    [ObservableProperty] private GridLength _minimum;
    [ObservableProperty] private GridLength _maximum;
    [ObservableProperty] private GridLength _average;
    [ObservableProperty] private GridLength _type;
    [ObservableProperty] private GridLength _provider;
    [ObservableProperty] private GridLength _quality;
    [ObservableProperty] private GridLength _sensorId;
    [ObservableProperty] private GridLength _samples;
    [ObservableProperty] private bool _isAdvanced;

    public void Apply(SensorExplorerState state)
    {
        static GridLength W(bool visible, double width) => new(visible ? width : 0);
        Value = W(state.IsColumnVisible(ExplorerColumns.Value), 100);
        Minimum = W(state.IsColumnVisible(ExplorerColumns.Minimum), 88);
        Maximum = W(state.IsColumnVisible(ExplorerColumns.Maximum), 88);
        Average = W(state.IsColumnVisible(ExplorerColumns.Average), 88);
        Type = W(state.IsColumnVisible(ExplorerColumns.Type), 100);
        Provider = W(state.IsColumnVisible(ExplorerColumns.Provider), 150);
        Quality = W(state.IsColumnVisible(ExplorerColumns.Quality), 76);
        SensorId = W(state.IsColumnVisible(ExplorerColumns.SensorId), 220);
        Samples = W(state.IsColumnVisible(ExplorerColumns.Samples), 76);
        IsAdvanced = state.DisplayMode == ExplorerDisplayMode.Advanced;
    }
}

/// <summary>One visible row of the explorer, cached by stable key and updated in place.</summary>
public sealed partial class ExplorerRow : ObservableObject
{
    public ExplorerRow(string key, ExplorerLayout layout)
    {
        Key = key;
        Layout = layout;
    }

    public string Key { get; }
    public ExplorerLayout Layout { get; }
    public ExplorerNodeKind Kind { get; private set; }
    public string? SensorId { get; private set; }
    public string? GroupKey { get; private set; }
    public bool IsContainer => Kind != ExplorerNodeKind.Sensor;
    public bool IsSensor => Kind == ExplorerNodeKind.Sensor;
    public double Indent => Depth * 16;
    public int Depth { get; private set; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _icon = "Other";
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private int _childCount;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isHidden;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _current = string.Empty;
    [ObservableProperty] private string _minimum = string.Empty;
    [ObservableProperty] private string _maximum = string.Empty;
    [ObservableProperty] private string _average = string.Empty;
    [ObservableProperty] private string _typeName = string.Empty;
    [ObservableProperty] private string _provider = string.Empty;
    [ObservableProperty] private string _samples = string.Empty;
    [ObservableProperty] private DataQuality _quality;
    [ObservableProperty] private HwuStatus _status;
    [ObservableProperty] private string _detail = string.Empty;

    internal void Configure(ExplorerNode node)
    {
        Kind = node.Kind;
        Depth = node.Depth;
        SensorId = node.Sensor?.SensorId;
        GroupKey = node.GroupKey;
        IsExpanded = node.IsExpanded;
        ChildCount = node.ChildCount;
        IsFavorite = node.IsFavorite;
        IsPinned = node.IsPinned;
        IsHidden = node.IsHidden;
        IsAvailable = node.IsAvailable;
        (Title, Icon) = node.Kind switch
        {
            ExplorerNodeKind.Category when node.Key == ExplorerKeys.Favorites => (Loc.Get("Explorer_Favorites"), "Star"),
            ExplorerNodeKind.Category => (Loc.Get($"Category_{node.Category}"), IconMap.ForCategory(node.Category)),
            ExplorerNodeKind.Device => (node.Title, IconMap.ForCategory(node.Category)),
            ExplorerNodeKind.Group => (Loc.Get($"Group_{node.Group}"), SensorExplorerViewModel.GroupIcon(node.Group!.Value)),
            _ => (node.Title, SensorExplorerViewModel.TypeIcon(node.Sensor!.Type)),
        };
        if (node.Kind == ExplorerNodeKind.Device && node.Device is { } d)
        {
            // Identification line: PCI ids when known, plus every provider contributing to this physical device.
            Subtitle = string.Join("  ·  ", new[] { d.Identity?.PciIdText, string.Join(" + ", d.Providers) }.Where(x => !string.IsNullOrEmpty(x)));
            Status = node.IsAvailable ? HwuStatus.Normal : HwuStatus.Inactive;
        }

        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Indent));
        if (node.Sensor is { } s) Apply(s);
    }

    internal void Apply(SensorReading r)
    {
        var c = Loc.Instance.Culture;
        var na = Loc.Get("Common_NA");
        Current = Format(r.Unit, r.Value, c, na);
        Minimum = Format(r.Unit, r.Minimum, c, na);
        Maximum = Format(r.Unit, r.Maximum, c, na);
        Average = Format(r.Unit, r.Average, c, na);
        Quality = r.Quality;
        IsAvailable = r.Value.HasValue;
        Samples = r.SampleCount.ToString("N0", c);
        if (TypeName.Length == 0)
        {
            TypeName = Loc.Get($"SensorType_{r.Type}");
            Provider = r.AlsoReportedBy.Count == 0 ? r.ProviderName : $"{r.ProviderName} (+{string.Join(", ", r.AlsoReportedBy)})";
            Detail = r.Kind == SensorKind.Metadata
                ? $"{r.SensorId}  ·  {Loc.Get("Explorer_MetadataNote")}"
                : $"{r.SensorId}  ·  {Provider}";
        }

        Status = r.Quality switch
        {
            DataQuality.Suspect or DataQuality.Stale => HwuStatus.Warning,
            _ => HwuStatus.Inactive,
        };
    }

    private static string Format(SensorUnit unit, double? value, IFormatProvider culture, string na) =>
        unit == SensorUnit.Flag && value is { } f ? (f != 0 ? Loc.Get("Explorer_FlagActive") : "—") : unit.FormatValue(value, culture, na);
}

public sealed record FilterOption<T>(T? Value, string Label);

public sealed partial class SensorExplorerViewModel : ObservableObject
{
    private readonly ISensorPollingService _polling;
    private readonly IPreferenceStore<SensorExplorerState> _store;
    private readonly Dictionary<string, ExplorerRow> _cache = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer;
    private volatile SensorSnapshot _latest = SensorSnapshot.Empty;
    private SensorSnapshot? _applied;
    private (int Sensors, int Devices, int Version) _signature = (-1, -1, -1);
    private int _version;
    private IReadOnlyList<ExplorerNode> _nodes = [];

    public SensorExplorerViewModel(ISensorPollingService polling, IPreferenceStore<SensorExplorerState> store)
    {
        _polling = polling;
        _store = store;
        var loaded = store.Load();
        State = loaded.Sanitize();
        _isAdvanced = State.DisplayMode == ExplorerDisplayMode.Advanced;
        _showHidden = State.ShowHidden;
        _refreshIntervalMs = State.RefreshIntervalMs;
        Layout.Apply(State);

        SortOptions = Enum.GetValues<SensorSortColumn>().Select(c => new FilterOption<SensorSortColumn?>(c, Loc.Get($"Sort_{c}"))).ToList();
        _sortOption = SortOptions.First(o => o.Value == State.SortColumn);
        StatusFilters = Enum.GetValues<SensorStatusFilter>().Select(f => new FilterOption<SensorStatusFilter?>(f, Loc.Get($"StatusFilter_{f}"))).ToList();
        _statusFilter = StatusFilters[0];
        GroupFilters = [new(null, Loc.Get("Explorer_AllGroups")), .. Enum.GetValues<SensorGroupKind>().Select(g => new FilterOption<SensorGroupKind?>(g, Loc.Get($"Group_{g}")))];
        CategoryFilters = [new(null, Loc.Get("Explorer_AllCategories")), .. SensorExplorerBuilder.CategoryOrder.Where(c => c is not HardwareCategory.SuperIo and not HardwareCategory.EmbeddedController)
            .Select(c => new FilterOption<HardwareCategory?>(c, Loc.Get($"Category_{c}")))];
        _groupFilter = GroupFilters[0];
        _categoryFilter = CategoryFilters[0];
        _deviceFilter = new FilterOption<string>(null, Loc.Get("Explorer_AllDevices"));
        _providerFilter = new FilterOption<string>(null, Loc.Get("Explorer_AllProviders"));
        DeviceFilters.Add(_deviceFilter);
        ProviderFilters.Add(_providerFilter);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(_refreshIntervalMs) };
        _timer.Tick += (_, _) => ApplyLatest();
        Loc.Instance.PropertyChanged += (_, _) => { _cache.Clear(); Invalidate(); };
        UI.Themes.ThemeManager.Changed += (_, _) => OnPropertyChanged(nameof(Layout));
    }

    public SensorExplorerState State { get; }
    public ExplorerLayout Layout { get; } = new();
    public RangeObservableCollection<ExplorerRow> Rows { get; } = [];
    public IReadOnlyList<FilterOption<SensorSortColumn?>> SortOptions { get; }
    public IReadOnlyList<FilterOption<SensorStatusFilter?>> StatusFilters { get; }
    public IReadOnlyList<FilterOption<SensorGroupKind?>> GroupFilters { get; }
    public IReadOnlyList<FilterOption<HardwareCategory?>> CategoryFilters { get; }
    public RangeObservableCollection<FilterOption<string>> DeviceFilters { get; } = [];
    public RangeObservableCollection<FilterOption<string>> ProviderFilters { get; } = [];
    public IReadOnlyList<int> RefreshIntervals => SensorExplorerState.RefreshIntervals;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FilterOption<SensorGroupKind?> _groupFilter;
    [ObservableProperty] private FilterOption<HardwareCategory?> _categoryFilter;
    [ObservableProperty] private FilterOption<string> _deviceFilter;
    [ObservableProperty] private FilterOption<string> _providerFilter;
    [ObservableProperty] private FilterOption<SensorStatusFilter?> _statusFilter;
    [ObservableProperty] private FilterOption<SensorSortColumn?> _sortOption;
    [ObservableProperty] private bool _isAdvanced;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private int _refreshIntervalMs;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private int _activeFilterCount;
    [ObservableProperty] private ExplorerRow? _selectedRow;

    public bool SortDescending
    {
        get => State.SortDescending;
        set { State.SortDescending = value; OnPropertyChanged(); Invalidate(); }
    }

    public bool ShowValue { get => State.IsColumnVisible(ExplorerColumns.Value); set => SetColumn(ExplorerColumns.Value, value); }
    public bool ShowMinimum { get => State.IsColumnVisible(ExplorerColumns.Minimum); set => SetColumn(ExplorerColumns.Minimum, value); }
    public bool ShowMaximum { get => State.IsColumnVisible(ExplorerColumns.Maximum); set => SetColumn(ExplorerColumns.Maximum, value); }
    public bool ShowAverage { get => State.IsColumnVisible(ExplorerColumns.Average); set => SetColumn(ExplorerColumns.Average, value); }
    public bool ShowType { get => State.IsColumnVisible(ExplorerColumns.Type); set => SetColumn(ExplorerColumns.Type, value); }
    public bool ShowProvider { get => State.IsColumnVisible(ExplorerColumns.Provider); set => SetColumn(ExplorerColumns.Provider, value); }
    public bool ShowQuality { get => State.IsColumnVisible(ExplorerColumns.Quality); set => SetColumn(ExplorerColumns.Quality, value); }
    public bool ShowSensorId { get => State.IsColumnVisible(ExplorerColumns.SensorId); set => SetColumn(ExplorerColumns.SensorId, value); }
    public bool ShowSamples { get => State.IsColumnVisible(ExplorerColumns.Samples); set => SetColumn(ExplorerColumns.Samples, value); }

    /// <summary>True while the Sensors page is on screen; off-screen the explorer does no UI work.</summary>
    public bool IsActive
    {
        get => _timer.IsEnabled;
        set
        {
            if (value == _timer.IsEnabled) return;
            if (value) { _timer.Start(); ApplyLatest(); }
            else _timer.Stop();
        }
    }

    // ------------------------------------------------------------------ data flow
    /// <summary>Thread-safe: called from the polling thread; only the newest snapshot is applied on the next tick.</summary>
    public void Update(SensorSnapshot snapshot) => _latest = snapshot;

    private void ApplyLatest()
    {
        var snap = _latest;
        if (ReferenceEquals(snap, _applied)) return;
        _applied = snap;
        var sig = (snap.Sensors.Count, snap.Devices.Count, _version);
        var valueSorted = State.SortColumn is not (SensorSortColumn.Default or SensorSortColumn.Name);
        if (sig != _signature || valueSorted)
        {
            if (sig.Item1 != _signature.Sensors || sig.Item2 != _signature.Devices) UpdateFilterLists(snap);
            _signature = sig;
            Rebuild(snap);
        }
        else
        {
            foreach (var row in Rows)
            {
                if (row.SensorId is { } id && snap.Find(id) is { } reading) row.Apply(reading);
            }
        }
    }

    private void Invalidate()
    {
        _version++;
        _applied = null;
        ApplyLatest();
        _store.Save(State);
    }

    private ExplorerFilter CurrentFilter() => new(SearchText, GroupFilter.Value, CategoryFilter.Value, DeviceFilter.Value, ProviderFilter.Value,
        StatusFilter.Value ?? SensorStatusFilter.All, IncludeMetadata: IsAdvanced);

    private void Rebuild(SensorSnapshot snap)
    {
        var filter = CurrentFilter();
        _nodes = SensorExplorerBuilder.Build(snap, State, filter);
        var rows = new List<ExplorerRow>(_nodes.Count);
        foreach (var node in _nodes)
        {
            if (!_cache.TryGetValue(node.Key, out var row)) _cache[node.Key] = row = new ExplorerRow(node.Key, Layout);
            row.Configure(node);
            rows.Add(row);
        }

        Rows.ReplaceAll(rows);
        IsEmpty = rows.Count == 0;
        ActiveFilterCount = new object?[] { GroupFilter.Value, CategoryFilter.Value, DeviceFilter.Value, ProviderFilter.Value }.Count(v => v is not null)
                            + (StatusFilter.Value is SensorStatusFilter.All or null ? 0 : 1);
        Summary = Loc.Format("Explorer_Count", snap.Sensors.Count(s => s.IsMeasurement), snap.Devices.Count, State.HiddenSensors.Count, State.FavoriteSensors.Count);
    }

    private void UpdateFilterLists(SensorSnapshot snap)
    {
        var device = DeviceFilter;
        var provider = ProviderFilter;
        DeviceFilters.ReplaceAll([DeviceFilters[0], .. snap.Devices.OrderBy(d => d.Category).Select(d => new FilterOption<string>(d.DeviceId, d.Name))]);
        ProviderFilters.ReplaceAll([ProviderFilters[0], .. snap.Sensors.Select(s => s.ProviderName).Distinct().Order().Select(p => new FilterOption<string>(p, p))]);
        DeviceFilter = DeviceFilters.FirstOrDefault(d => d.Value == device.Value) ?? DeviceFilters[0];
        ProviderFilter = ProviderFilters.FirstOrDefault(p => p.Value == provider.Value) ?? ProviderFilters[0];
    }

    partial void OnSearchTextChanged(string value) => Invalidate();
    partial void OnGroupFilterChanged(FilterOption<SensorGroupKind?> value) => Invalidate();
    partial void OnCategoryFilterChanged(FilterOption<HardwareCategory?> value) => Invalidate();
    partial void OnDeviceFilterChanged(FilterOption<string> value) { if (value is not null) Invalidate(); }
    partial void OnProviderFilterChanged(FilterOption<string> value) { if (value is not null) Invalidate(); }
    partial void OnStatusFilterChanged(FilterOption<SensorStatusFilter?> value) => Invalidate();

    partial void OnSortOptionChanged(FilterOption<SensorSortColumn?> value)
    {
        State.SortColumn = value.Value ?? SensorSortColumn.Default;
        Invalidate();
    }

    partial void OnIsAdvancedChanged(bool value)
    {
        State.DisplayMode = value ? ExplorerDisplayMode.Advanced : ExplorerDisplayMode.Compact;
        Layout.Apply(State);
        Invalidate(); // metadata group appears / disappears
    }

    partial void OnShowHiddenChanged(bool value)
    {
        State.ShowHidden = value;
        Invalidate();
    }

    partial void OnRefreshIntervalMsChanged(int value)
    {
        State.RefreshIntervalMs = value;
        _timer.Interval = TimeSpan.FromMilliseconds(value);
        _store.Save(State);
    }

    private void SetColumn(string column, bool visible)
    {
        State.SetColumnVisible(column, visible);
        Layout.Apply(State);
        OnPropertyChanged($"Show{column}");
        _store.Save(State);
    }

    // ------------------------------------------------------------------ commands
    [RelayCommand]
    private void Toggle(ExplorerRow? row)
    {
        if (row is null || !row.IsContainer) return;
        State.Toggle(row.Key);
        Invalidate();
    }

    [RelayCommand]
    private void ExpandAll() { State.ExpandAll(); Invalidate(); }

    [RelayCommand]
    private void CollapseAll() { State.CollapseAll(SensorExplorerBuilder.ContainerKeys(_latest)); Invalidate(); }

    [RelayCommand]
    private void ToggleFavorite(ExplorerRow? row) { if (row?.SensorId is { } id) { State.ToggleFavorite(id); Invalidate(); } }

    [RelayCommand]
    private void TogglePinned(ExplorerRow? row) { if (row?.SensorId is { } id) { State.TogglePinned(id); Invalidate(); } }

    [RelayCommand]
    private void ToggleHidden(ExplorerRow? row) { if (row?.SensorId is { } id) { State.SetHidden(id, !State.IsHidden(id)); Invalidate(); } }

    [RelayCommand]
    public void UnhideAll() { State.UnhideAll(); Invalidate(); }

    [RelayCommand]
    public void ClearFavorites() { State.FavoriteSensors.Clear(); State.PinnedSensors.Clear(); Invalidate(); }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        GroupFilter = GroupFilters[0];
        CategoryFilter = CategoryFilters[0];
        DeviceFilter = DeviceFilters[0];
        ProviderFilter = ProviderFilters[0];
        StatusFilter = StatusFilters[0];
    }

    [RelayCommand]
    private void MoveUp(ExplorerRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(ExplorerRow? row) => Move(row, +1);

    private void Move(ExplorerRow? row, int delta)
    {
        if (row?.SensorId is not { } id || row.GroupKey is not { } group) return;
        if (State.SortColumn != SensorSortColumn.Default)
        {
            // Manual ordering is the custom order; switch back to it so the move is visible.
            SortOption = SortOptions[0];
            SortDescending = false;
        }

        if (State.Move(group, SensorExplorerBuilder.GroupOrder(_nodes, group), id, delta)) Invalidate();
    }

    [RelayCommand]
    public void ResetOrder() { State.ResetOrder(); Invalidate(); }

    [RelayCommand]
    private void ResetAll() => _polling.ResetStatistics();

    [RelayCommand]
    private void ResetSensor(ExplorerRow? row) { if (row?.SensorId is { } id) _polling.ResetStatistics(id); }

    [RelayCommand]
    private static void CopyValue(ExplorerRow? row) { if (row is not null) ClipboardService.SetText(row.IsSensor ? row.Current : row.Title); }

    [RelayCommand]
    private static void CopyRow(ExplorerRow? row)
    {
        if (row is null) return;
        ClipboardService.SetText(row.IsSensor ? $"{row.Title}\t{row.Current}\t{row.Minimum}\t{row.Maximum}\t{row.Average}\t{row.Provider}" : row.Title);
    }

    // ------------------------------------------------------------------ icons
    public static string GroupIcon(SensorGroupKind g) => g switch
    {
        SensorGroupKind.Temperatures or SensorGroupKind.Hotspot or SensorGroupKind.MemoryTemperatures => "Thermometer",
        SensorGroupKind.Voltages => "Voltage",
        SensorGroupKind.Currents => "Current",
        SensorGroupKind.Powers or SensorGroupKind.PowerLimits or SensorGroupKind.Energy => "Power",
        SensorGroupKind.Clocks or SensorGroupKind.MemoryClocks => "Clock",
        SensorGroupKind.Utilization or SensorGroupKind.Activity => "Utilization",
        SensorGroupKind.PerCore => "PerCore",
        SensorGroupKind.Throttling or SensorGroupKind.PerformanceLimits => "Throttle",
        SensorGroupKind.Fans or SensorGroupKind.FanControl => "Fan",
        SensorGroupKind.Memory or SensorGroupKind.Vram => "Memory",
        SensorGroupKind.Throughput => "Throughput",
        SensorGroupKind.Data => "Data",
        SensorGroupKind.Health => "Health",
        SensorGroupKind.Levels => "Level",
        SensorGroupKind.Timings => "Timing",
        _ => "Info",
    };

    public static string TypeIcon(SensorType t) => t switch
    {
        SensorType.Temperature => "Thermometer",
        SensorType.Voltage => "Voltage",
        SensorType.Current => "Current",
        SensorType.Power or SensorType.Energy => "Power",
        SensorType.Clock or SensorType.Frequency => "Clock",
        SensorType.Utilization => "Utilization",
        SensorType.FanSpeed or SensorType.FanControl => "Fan",
        SensorType.Flow or SensorType.Level or SensorType.Humidity => "Level",
        SensorType.Memory or SensorType.MemorySmall => "Data",
        SensorType.Throughput => "Throughput",
        SensorType.PerformanceLimit => "Throttle",
        SensorType.Duration or SensorType.Timing => "Timing",
        _ => "Info",
    };
}
