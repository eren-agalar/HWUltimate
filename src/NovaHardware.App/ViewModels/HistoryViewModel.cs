using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.History;
using NovaHardware.Domain.Sensors;
using NovaHardware.UI.Localization;
using NovaHardware.UI.Themes;

namespace NovaHardware.App.ViewModels;

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoricalDataService _history;
    private CancellationTokenSource? _loadCts;

    public HistoryViewModel(IHistoricalDataService history)
    {
        _history = history;
        SensorView = CollectionViewSource.GetDefaultView(Sensors);
        SensorView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorDescriptor.DeviceName)));
        SensorView.Filter = o => o is SensorDescriptor d && (string.IsNullOrWhiteSpace(Filter)
            || d.Name.Contains(Filter, StringComparison.CurrentCultureIgnoreCase)
            || d.DeviceName.Contains(Filter, StringComparison.CurrentCultureIgnoreCase));
        ThemeManager.Changed += (_, _) => _ = LoadSeriesAsync();
    }

    public ObservableCollection<SensorDescriptor> Sensors { get; } = [];
    public ICollectionView SensorView { get; }

    public IReadOnlyList<(string Key, TimeSpan Range)> Ranges { get; } =
    [
        ("Hist_15m", TimeSpan.FromMinutes(15)), ("Hist_1h", TimeSpan.FromHours(1)), ("Hist_6h", TimeSpan.FromHours(6)),
        ("Hist_24h", TimeSpan.FromHours(24)), ("Hist_7d", TimeSpan.FromDays(7)),
    ];

    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private SensorDescriptor? _selected;
    [ObservableProperty] private string _rangeKey = "Hist_1h";
    [ObservableProperty] private ISeries[] _series = [];
    [ObservableProperty] private Axis[] _xAxes = [ChartTheme.TimeAxis("HH:mm")];
    [ObservableProperty] private Axis[] _yAxes = [ChartTheme.ValueAxis()];
    [ObservableProperty] private string _statMin = "—";
    [ObservableProperty] private string _statMax = "—";
    [ObservableProperty] private string _statAvg = "—";
    [ObservableProperty] private string _statCount = string.Empty;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _databaseInfo = string.Empty;

    partial void OnFilterChanged(string value) => SensorView.Refresh();
    partial void OnSelectedChanged(SensorDescriptor? value) => _ = LoadSeriesAsync();
    partial void OnRangeKeyChanged(string value) => _ = LoadSeriesAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var list = await _history.GetSensorsAsync();
        var selectedId = Selected?.SensorId;
        Sensors.Clear();
        foreach (var d in list) Sensors.Add(d);
        Selected = Sensors.FirstOrDefault(s => s.SensorId == selectedId);
        DatabaseInfo = Loc.Format("Hist_Database", await _history.GetSampleCountAsync());
        await LoadSeriesAsync();
    }

    [RelayCommand]
    private void SetRange(string key) => RangeKey = key;

    private async Task LoadSeriesAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        var sensor = Selected;
        if (sensor is null)
        {
            Series = [];
            HasData = false;
            return;
        }

        var range = Ranges.First(r => r.Key == RangeKey).Range;
        var to = DateTimeOffset.Now;
        var from = to - range;
        try
        {
            var buckets = await _history.GetAggregatedAsync(new HistoryQuery(sensor.SensorId, from, to), token);
            var stats = await _history.GetStatisticsAsync(sensor.SensorId, from, to, token);
            if (token.IsCancellationRequested) return;

            var unit = sensor.Unit.ToSymbol();
            Series =
            [
                ChartTheme.Line(Loc.Get("Common_Max"), buckets.Select(b => new DateTimePoint(b.BucketStart.LocalDateTime, b.Maximum)).ToList(), "Brush.Bad", fill: false),
                ChartTheme.Line(Loc.Get("Common_Avg"), buckets.Select(b => new DateTimePoint(b.BucketStart.LocalDateTime, b.Average)).ToList(), "Brush.Series1"),
                ChartTheme.Line(Loc.Get("Common_Min"), buckets.Select(b => new DateTimePoint(b.BucketStart.LocalDateTime, b.Minimum)).ToList(), "Brush.Good", fill: false),
            ];
            XAxes = [ChartTheme.TimeAxis(range > TimeSpan.FromDays(1) ? "dd.MM HH:mm" : "HH:mm")];
            YAxes = [ChartTheme.ValueAxis(unit: unit.Length > 0 ? unit : null)];
            HasData = buckets.Count > 0;
            var c = Loc.Instance.Culture;
            StatMin = sensor.Unit.FormatValue(stats?.Minimum, c, "—");
            StatMax = sensor.Unit.FormatValue(stats?.Maximum, c, "—");
            StatAvg = sensor.Unit.FormatValue(stats?.Average, c, "—");
            StatCount = stats is null ? string.Empty : Loc.Format("Hist_Samples", stats.Count);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
