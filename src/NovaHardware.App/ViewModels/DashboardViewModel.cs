using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using NovaHardware.App.Services;
using NovaHardware.Core.Sensors;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using NovaHardware.UI.Localization;
using NovaHardware.UI.Themes;

namespace NovaHardware.App.ViewModels;

/// <summary>A headline metric. <see cref="Text"/> is "N/A" whenever the hardware does not report the value.</summary>
public sealed partial class Metric : ObservableObject
{
    [ObservableProperty] private string _text = Loc.Get("Common_NA");
    [ObservableProperty] private double? _value;
    [ObservableProperty] private string? _tooltip;

    public void Set(SensorReading? r)
    {
        Value = r?.Value;
        Text = r is null ? Loc.Get("Common_NA") : r.Unit.FormatValue(r.Value, Loc.Instance.Culture, Loc.Get("Common_NA"));
        Tooltip = r is null ? null : $"{r.Name} · {r.ProviderName}";
    }
}

public sealed record NamedValue(string Device, string Name, string Value, double? Raw);

public sealed partial class DashboardViewModel : ObservableObject
{
    private const int WindowSeconds = 120;

    public DashboardViewModel()
    {
        BuildCharts();
        ThemeManager.Changed += (_, _) => BuildCharts();
        Loc.Instance.PropertyChanged += (_, _) => BuildCharts();
    }

    [ObservableProperty] private string _cpuName = "—";
    [ObservableProperty] private string _gpuName = "—";
    [ObservableProperty] private ObservableCollection<ProviderStatusItem> _providers = [];
    [ObservableProperty] private ISeries[] _loadSeries = [];
    [ObservableProperty] private ISeries[] _tempSeries = [];
    [ObservableProperty] private Axis[] _loadXAxes = [];
    [ObservableProperty] private Axis[] _tempXAxes = [];
    [ObservableProperty] private Axis[] _loadYAxes = [];
    [ObservableProperty] private Axis[] _tempYAxes = [];

    public Metric CpuLoad { get; } = new();
    public Metric CpuTemp { get; } = new();
    public Metric CpuPower { get; } = new();
    public Metric CpuClock { get; } = new();
    public Metric GpuLoad { get; } = new();
    public Metric GpuTemp { get; } = new();
    public Metric GpuPower { get; } = new();
    public Metric GpuClock { get; } = new();
    public Metric MemLoad { get; } = new();
    public Metric MemUsed { get; } = new();
    public Metric MemAvailable { get; } = new();

    public ObservableCollection<NamedValue> StorageTemps { get; } = [];
    public ObservableCollection<NamedValue> Fans { get; } = [];

    private readonly ObservableCollection<DateTimePoint> _cpuLoadPts = [];
    private readonly ObservableCollection<DateTimePoint> _gpuLoadPts = [];
    private readonly ObservableCollection<DateTimePoint> _memLoadPts = [];
    private readonly ObservableCollection<DateTimePoint> _cpuTempPts = [];
    private readonly ObservableCollection<DateTimePoint> _gpuTempPts = [];

    private void BuildCharts()
    {
        LoadSeries =
        [
            ChartTheme.Line(Loc.Get("Dash_Processor"), _cpuLoadPts, "Brush.Series1"),
            ChartTheme.Line(Loc.Get("Dash_Graphics"), _gpuLoadPts, "Brush.Series2"),
            ChartTheme.Line(Loc.Get("Dash_Memory"), _memLoadPts, "Brush.Series3", fill: false),
        ];
        TempSeries =
        [
            ChartTheme.Line(Loc.Get("Dash_Processor"), _cpuTempPts, "Brush.Series1"),
            ChartTheme.Line(Loc.Get("Dash_Graphics"), _gpuTempPts, "Brush.Series2"),
        ];
        LoadXAxes = [ChartTheme.TimeAxis()];
        TempXAxes = [ChartTheme.TimeAxis()];
        LoadYAxes = [ChartTheme.ValueAxis(0, 100, "%")];
        TempYAxes = [ChartTheme.ValueAxis(unit: "°C")];
    }

    public void Update(SensorSnapshot s)
    {
        CpuName = s.DevicesOf(HardwareCategory.Cpu).FirstOrDefault()?.Name ?? "—";
        GpuName = string.Join(" · ", s.DevicesOf(HardwareCategory.Gpu).Select(d => d.Name).Distinct()) is { Length: > 0 } g ? g : "—";

        CpuLoad.Set(s.CpuLoad());
        CpuTemp.Set(s.CpuTemperature());
        CpuPower.Set(s.CpuPower());
        CpuClock.Set(s.CpuClock());
        GpuLoad.Set(s.GpuLoad());
        GpuTemp.Set(s.GpuTemperature());
        GpuPower.Set(s.GpuPower());
        GpuClock.Set(s.GpuClock());
        MemLoad.Set(s.MemoryLoad());
        MemUsed.Set(s.MemoryUsed());
        MemAvailable.Set(s.MemoryAvailable());

        var t = s.Timestamp.LocalDateTime;
        Push(_cpuLoadPts, t, CpuLoad.Value);
        Push(_gpuLoadPts, t, GpuLoad.Value);
        Push(_memLoadPts, t, MemLoad.Value);
        Push(_cpuTempPts, t, CpuTemp.Value);
        Push(_gpuTempPts, t, GpuTemp.Value);

        var names = s.Devices.ToDictionary(d => d.DeviceId, d => d.Name, StringComparer.Ordinal);
        var storageIds = s.DevicesOf(HardwareCategory.Storage).Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
        Sync(StorageTemps, s.Sensors.Where(x => x.Type == SensorType.Temperature && storageIds.Contains(x.DeviceId) && x.Value.HasValue)
            .GroupBy(x => x.DeviceId).Select(gr => gr.First())
            .Select(x => new NamedValue(names.GetValueOrDefault(x.DeviceId, x.DeviceId), x.Name, x.Unit.FormatValue(x.Value, Loc.Instance.Culture), x.Value)));
        Sync(Fans, s.Sensors.Where(x => x.Type is SensorType.FanSpeed or SensorType.Flow && x.Value is > 0)
            .Select(x => new NamedValue(names.GetValueOrDefault(x.DeviceId, x.DeviceId), x.Name, x.Unit.FormatValue(x.Value, Loc.Instance.Culture), x.Value)));
    }

    /// <summary>Appends a real point. Gaps (missing values) are left as gaps instead of being interpolated.</summary>
    private static void Push(ObservableCollection<DateTimePoint> pts, DateTime t, double? v)
    {
        pts.Add(new DateTimePoint(t, v));
        var cutoff = t.AddSeconds(-WindowSeconds);
        while (pts.Count > 0 && pts[0].DateTime < cutoff)
        {
            pts.RemoveAt(0);
        }
    }

    private static void Sync(ObservableCollection<NamedValue> target, IEnumerable<NamedValue> source)
    {
        var list = source.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (i < target.Count)
            {
                if (target[i] != list[i]) target[i] = list[i];
            }
            else
            {
                target.Add(list[i]);
            }
        }

        while (target.Count > list.Count) target.RemoveAt(target.Count - 1);
    }
}
