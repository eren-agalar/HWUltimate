using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Explorer;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using NovaHardware.UI.Controls;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

/// <summary>Column widths and row height shared by the header and every row (rows bind to it through their DataContext).</summary>
public sealed partial class ExplorerLayout : ObservableObject
{
    [ObservableProperty] private GridLength _value;
    [ObservableProperty] private GridLength _minimum;
    [ObservableProperty] private GridLength _maximum;
    [ObservableProperty] private GridLength _average;
    [ObservableProperty] private GridLength _type;
    [ObservableProperty] private GridLength _provider;
    [ObservableProperty] private GridLength _quality;
    [ObservableProperty] private double _rowHeight = 24;
    [ObservableProperty] private bool _isDetailed;

    public void Apply(SensorExplorerState state)
    {
        static GridLength W(bool visible, double width) => new(visible ? width : 0);
        Value = W(state.IsColumnVisible(ExplorerColumns.Value), 112);
        Minimum = W(state.IsColumnVisible(ExplorerColumns.Minimum), 100);
        Maximum = W(state.IsColumnVisible(ExplorerColumns.Maximum), 100);
        Average = W(state.IsColumnVisible(ExplorerColumns.Average), 100);
        Type = W(state.IsColumnVisible(ExplorerColumns.Type), 110);
        Provider = W(state.IsColumnVisible(ExplorerColumns.Provider), 170);
        Quality = W(state.IsColumnVisible(ExplorerColumns.Quality), 80);
        IsDetailed = state.DisplayMode == ExplorerDisplayMode.Detailed;
        RowHeight = IsDetailed ? 40 : 24;
    }
}

/// <summary>One visible row of the explorer. Rows are cached by stable key and updated in place.</summary>
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
    public int Depth { get; private set; }
    public string? SensorId { get; private set; }
    public string? GroupKey { get; private set; }
    public bool IsContainer => Kind != ExplorerNodeKind.Sensor;
    public bool IsSensor => Kind == ExplorerNodeKind.Sensor;
    public double Indent => Depth * 18;
    public SensorUnit Unit { get; private set; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _icon = "Other";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private int _childCount;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isHidden;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _current = string.Empty;
    [ObservableProperty] private string _minimum = string.Empty;
    [ObservableProperty] private string _maximum = string.Empty;
    [ObservableProperty] private string _average = string.Empty;
    [ObservableProperty] private string _typeName = string.Empty;
    [ObservableProperty] private string _provider = string.Empty;
    [ObservableProperty] private DataQuality _quality;
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
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Indent));
        if (node.Sensor is { } s) Apply(s);
    }

    internal void Apply(SensorReading r)
    {
        var c = Loc.Instance.Culture;
        var na = Loc.Get("Common_NA");
        Unit = r.Unit;
        Current = Format(r.Unit, r.Value, c, na);
        Minimum = Format(r.Unit, r.Minimum, c, na);
        Maximum = Format(r.Unit, r.Maximum, c, na);
        Average = Format(r.Unit, r.Average, c, na);
        Quality = r.Quality;
        IsAvailable = r.Value.HasValue;
        if (TypeName.Length == 0)
        {
            TypeName = r.Type.ToString();
            Provider = r.ProviderName;
        }

        Detail = $"{r.SensorId}  ·  {Loc.Format("Explorer_Samples", r.SampleCount)}  ·  {r.UnitSymbol}";
    }

    private static string Format(SensorUnit unit, double? value, IFormatProvider culture, string na) =>
        unit == SensorUnit.Flag && value is { } f ? (f != 0 ? "●" : "—") : unit.FormatValue(value, culture, na);
}

public sealed record FilterOption<T>(T? Value, string Label);

public sealed partial class SensorExplorerViewModel : ObservableObject
{
    private readonly ISensorPollingService _polling;
    private readonly IPreferenceStore<SensorExplorerState> _store;
    private readonly Dictionary<string, ExplorerRow> _cache = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer;
    private SensorSnapshot _latest = SensorSnapshot.Empty;
    private SensorSnapshot? _applied;
    private (int Sensors, int Devices, int Version) _signature = (-1, -1, -1);
    private int _version;
    private IReadOnlyList<ExplorerNode> _nodes = [];

    public SensorExplorerViewModel(ISensorPollingService polling, IPreferenceStore<SensorExplorerState> store)
    {
        _polling = polling;
        _store = store;
        State = store.Load().Sanitize();
        _isDetailed = State.DisplayMode == ExplorerDisplayMode.Detailed;
        _showHidden = State.ShowHidden;
        _refreshIntervalMs = State.RefreshIntervalMs;
        Layout.Apply(State);

        GroupFilters = [new(null, Loc.Get("Explorer_AllGroups")), .. Enum.GetValues<SensorGroupKind>().Select(g => new FilterOption<SensorGroupKind?>(g, Loc.Get($"Group_{g}")))];
        CategoryFilters = [new(null, Loc.Get("Explorer_AllCategories")), .. SensorExplorerBuilder.CategoryOrder.Where(c => c is not HardwareCategory.SuperIo and not HardwareCategory.EmbeddedController)
            .Select(c => new FilterOption<HardwareCategory?>(c, Loc.Get($"Category_{c}")))];
        _groupFilter = GroupFilters[0];
        _categoryFilter = CategoryFilters[0];

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(_refreshIntervalMs) };
        _timer.Tick += (_, _) => ApplyLatest();
        Loc.Instance.PropertyChanged += (_, _) => Invalidate();
    }

    public SensorExplorerState State { get; }
    public ExplorerLayout Layout { get; } = new();
    public RangeObservableCollection<ExplorerRow> Rows { get; } = [];
    public IReadOnlyList<FilterOption<SensorGroupKind?>> GroupFilters { get; }
    public IReadOnlyList<FilterOption<HardwareCategory?>> CategoryFilters { get; }
    public IReadOnlyList<int> RefreshIntervals => SensorExplorerState.RefreshIntervals;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FilterOption<SensorGroupKind?> _groupFilter;
    [ObservableProperty] private FilterOption<HardwareCategory?> _categoryFilter;
    [ObservableProperty] private bool _isDetailed;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private int _refreshIntervalMs;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private ExplorerRow? _selectedRow;

    public bool ShowValue { get => State.IsColumnVisible(ExplorerColumns.Value); set => SetColumn(ExplorerColumns.Value, value); }
    public bool ShowMinimum { get => State.IsColumnVisible(ExplorerColumns.Minimum); set => SetColumn(ExplorerColumns.Minimum, value); }
    public bool ShowMaximum { get => State.IsColumnVisible(ExplorerColumns.Maximum); set => SetColumn(ExplorerColumns.Maximum, value); }
    public bool ShowAverage { get => State.IsColumnVisible(ExplorerColumns.Average); set => SetColumn(ExplorerColumns.Average, value); }
    public bool ShowType { get => State.IsColumnVisible(ExplorerColumns.Type); set => SetColumn(ExplorerColumns.Type, value); }
    public bool ShowProvider { get => State.IsColumnVisible(ExplorerColumns.Provider); set => SetColumn(ExplorerColumns.Provider, value); }
    public bool ShowQuality { get => State.IsColumnVisible(ExplorerColumns.Quality); set => SetColumn(ExplorerColumns.Quality, value); }

    public string SortIndicator(SensorSortColumn c) => State.SortColumn != c ? string.Empty : State.SortDescending ? " ▼" : " ▲";
    public string NameHeader => Loc.Get("Explorer_Name") + SortIndicator(SensorSortColumn.Name);
    public string ValueHeader => Loc.Get("Col_Value") + SortIndicator(SensorSortColumn.Value);
    public string MinimumHeader => Loc.Get("Col_Minimum") + SortIndicator(SensorSortColumn.Minimum);
    public string MaximumHeader => Loc.Get("Col_Maximum") + SortIndicator(SensorSortColumn.Maximum);
    public string AverageHeader => Loc.Get("Col_Average") + SortIndicator(SensorSortColumn.Average);

    /// <summary>True while the Sensors page is on screen; off-screen the explorer does no work.</summary>
    public bool IsActive
    {
        get => _timer.IsEnabled;
        set
        {
            if (value == _timer.IsEnabled) return;
            if (value)
            {
                _timer.Start();
                ApplyLatest();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    // ------------------------------------------------------------------ data flow
    /// <summary>Receives every snapshot; only the newest is applied on the next refresh tick.</summary>
    public void Update(SensorSnapshot snapshot) => _latest = snapshot;

    private void ApplyLatest()
    {
        var snap = _latest;
        if (ReferenceEquals(snap, _applied)) return;
        _applied = snap;
        var sig = (snap.Sensors.Count, snap.Devices.Count, _version);
        // Value-based sorting re-orders rows as values change, so it needs a structural pass every tick.
        if (sig != _signature || State.SortColumn is not (SensorSortColumn.Default or SensorSortColumn.Name))
        {
            _signature = sig;
            Rebuild(snap);
        }
        else
        {
            UpdateValues(snap);
        }
    }

    private void Invalidate()
    {
        _version++;
        _applied = null;
        ApplyLatest();
        SaveState();
    }

    private void Rebuild(SensorSnapshot snap)
    {
        var filter = new ExplorerFilter(SearchText, GroupFilter.Value, CategoryFilter.Value);
        _nodes = SensorExplorerBuilder.Build(snap, State, filter);
        var rows = new List<ExplorerRow>(_nodes.Count);
        foreach (var node in _nodes)
        {
            if (!_cache.TryGetValue(node.Key, out var row))
            {
                row = new ExplorerRow(node.Key, Layout);
                _cache[node.Key] = row;
            }

            row.Configure(node);
            rows.Add(row);
        }

        Rows.ReplaceAll(rows);
        IsEmpty = rows.Count == 0;
        Summary = Loc.Format("Explorer_Count", snap.Sensors.Count, snap.Devices.Count, State.HiddenSensors.Count, State.FavoriteSensors.Count);
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(ValueHeader));
        OnPropertyChanged(nameof(MinimumHeader));
        OnPropertyChanged(nameof(MaximumHeader));
        OnPropertyChanged(nameof(AverageHeader));
    }

    private void UpdateValues(SensorSnapshot snap)
    {
        foreach (var row in Rows)
        {
            if (row.SensorId is { } id && snap.Find(id) is { } reading)
            {
                row.Apply(reading);
            }
        }
    }

    // ------------------------------------------------------------------ preferences
    partial void OnSearchTextChanged(string value) => Invalidate();
    partial void OnGroupFilterChanged(FilterOption<SensorGroupKind?> value) => Invalidate();
    partial void OnCategoryFilterChanged(FilterOption<HardwareCategory?> value) => Invalidate();

    partial void OnIsDetailedChanged(bool value)
    {
        State.DisplayMode = value ? ExplorerDisplayMode.Detailed : ExplorerDisplayMode.Compact;
        Layout.Apply(State);
        SaveState();
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
        SaveState();
    }

    private void SetColumn(string column, bool visible)
    {
        State.SetColumnVisible(column, visible);
        Layout.Apply(State);
        OnPropertyChanged($"Show{column}");
        SaveState();
    }

    private void SaveState() => _store.Save(State);

    // ------------------------------------------------------------------ commands
    [RelayCommand]
    private void Toggle(ExplorerRow? row)
    {
        if (row is null || !row.IsContainer) return;
        State.Toggle(row.Key);
        Invalidate();
    }

    [RelayCommand]
    private void ExpandAll()
    {
        State.ExpandAll();
        Invalidate();
    }

    [RelayCommand]
    private void CollapseAll()
    {
        State.CollapseAll(SensorExplorerBuilder.ContainerKeys(_latest));
        Invalidate();
    }

    [RelayCommand]
    private void ToggleFavorite(ExplorerRow? row)
    {
        if (row?.SensorId is not { } id) return;
        State.ToggleFavorite(id);
        Invalidate();
    }

    [RelayCommand]
    private void ToggleHidden(ExplorerRow? row)
    {
        if (row?.SensorId is not { } id) return;
        State.SetHidden(id, !State.IsHidden(id));
        Invalidate();
    }

    [RelayCommand]
    private void UnhideAll()
    {
        State.UnhideAll();
        Invalidate();
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
            // Manual ordering applies to the custom order; switch back to it so the move is visible.
            State.SortColumn = SensorSortColumn.Default;
            State.SortDescending = false;
        }

        if (State.Move(group, SensorExplorerBuilder.GroupOrder(_nodes, group), id, delta)) Invalidate();
    }

    [RelayCommand]
    private void ResetOrder()
    {
        State.ResetOrder();
        Invalidate();
    }

    /// <summary>Cycles a column: ascending → descending → custom order.</summary>
    [RelayCommand]
    private void SortBy(string column)
    {
        var c = Enum.Parse<SensorSortColumn>(column);
        if (State.SortColumn != c)
        {
            State.SortColumn = c;
            State.SortDescending = false;
        }
        else if (!State.SortDescending)
        {
            State.SortDescending = true;
        }
        else
        {
            State.SortColumn = SensorSortColumn.Default;
            State.SortDescending = false;
        }

        Invalidate();
    }

    [RelayCommand]
    private void ResetAll() => _polling.ResetStatistics();

    [RelayCommand]
    private void ResetSensor(ExplorerRow? row)
    {
        if (row?.SensorId is { } id) _polling.ResetStatistics(id);
    }

    [RelayCommand]
    private static void CopyValue(ExplorerRow? row)
    {
        if (row is not null) ClipboardService.SetText(row.IsSensor ? row.Current : row.Title);
    }

    [RelayCommand]
    private static void CopyRow(ExplorerRow? row)
    {
        if (row is null) return;
        ClipboardService.SetText(row.IsSensor
            ? $"{row.Title}\t{row.Current}\t{row.Minimum}\t{row.Maximum}\t{row.Average}\t{row.Provider}"
            : row.Title);
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
