using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Sensors;
using NovaHardware.Core.Settings;
using NovaHardware.Core.Stress;
using NovaHardware.Domain.Sensors;
using NovaHardware.Domain.Stress;
using NovaHardware.UI.Localization;
using NovaHardware.UI.Themes;

namespace NovaHardware.App.ViewModels;

public sealed record StressKindOption(StressTestKind Kind, string Key, bool Supported, string? Note);

public sealed partial class StabilityViewModel : ObservableObject
{
    private readonly IStressTestService _stress;
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<DateTimePoint> _tempPts = [];
    private readonly ObservableCollection<DateTimePoint> _loadPts = [];

    public StabilityViewModel(IStressTestService stress, ISettingsService settings)
    {
        _stress = stress;
        Kinds = Enum.GetValues<StressTestKind>().Select(k =>
        {
            var ok = stress.IsSupported(k, out var reason);
            return new StressKindOption(k, k switch
            {
                StressTestKind.CpuFloatingPoint => "Stress_Cpu_Fp",
                StressTestKind.CpuInteger => "Stress_Cpu_Int",
                StressTestKind.CpuVector => "Stress_Cpu_Simd",
                StressTestKind.Memory => "Stress_Memory",
                _ => "Stress_Gpu",
            }, ok, ok ? (k == StressTestKind.CpuVector ? VectorWorkload.InstructionWidth : null) : reason);
        }).ToList();
        _selectedKind = Kinds[0];
        _thermalLimit = settings.Current.StressThermalLimitCelsius;
        _threads = Environment.ProcessorCount;
        BuildCharts();
        ThemeManager.Changed += (_, _) => BuildCharts();
    }

    public IReadOnlyList<StressKindOption> Kinds { get; }
    public IReadOnlyList<int> Durations { get; } = [1, 5, 10, 15, 30, 60, 120];
    public int MaxThreads { get; } = Environment.ProcessorCount;
    public ObservableCollection<StressTestResult> Results { get; } = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] private StressKindOption _selectedKind;
    [ObservableProperty] private int _durationMinutes = 10;
    [ObservableProperty] private int _threads;
    [ObservableProperty] private int _memoryMegabytes;
    [ObservableProperty] private double _thermalLimit;
    [ObservableProperty] private bool _stopOnError = true;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _elapsed = "00:00:00";
    [ObservableProperty] private string _iterations = "0";
    [ObservableProperty] private string _rate = "0";
    [ObservableProperty] private long _errors;
    [ObservableProperty] private string _maxTemp = "—";
    [ObservableProperty] private string _status = Loc.Get("Stress_Idle");
    [ObservableProperty] private StressTestOutcome? _lastOutcome;
    [ObservableProperty] private ISeries[] _series = [];
    [ObservableProperty] private Axis[] _xAxes = [];
    [ObservableProperty] private Axis[] _yAxes = [];

    private void BuildCharts()
    {
        Series =
        [
            ChartTheme.Line($"{Loc.Get("Dash_Temperature")} (°C)", _tempPts, "Brush.Bad"),
            ChartTheme.Line($"{Loc.Get("Dash_Load")} (%)", _loadPts, "Brush.Series1", fill: false),
        ];
        XAxes = [ChartTheme.TimeAxis()];
        YAxes = [ChartTheme.ValueAxis(0)];
    }

    public void Update(SensorSnapshot s)
    {
        if (!IsRunning) return;
        var t = s.Timestamp.LocalDateTime;
        _tempPts.Add(new DateTimePoint(t, s.MaxProcessorTemperature()));
        _loadPts.Add(new DateTimePoint(t, s.CpuLoad()?.Value));
        while (_tempPts.Count > 900) _tempPts.RemoveAt(0);
        while (_loadPts.Count > 900) _loadPts.RemoveAt(0);
    }

    private bool CanStart() => !IsRunning && SelectedKind.Supported;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _tempPts.Clear();
        _loadPts.Clear();
        IsRunning = true;
        Errors = 0;
        LastOutcome = null;
        Status = Loc.Get("Stress_Running");
        var options = new StressTestOptions
        {
            Kind = SelectedKind.Kind,
            Duration = TimeSpan.FromMinutes(DurationMinutes),
            Threads = Threads >= MaxThreads ? 0 : Threads,
            MemoryMegabytes = MemoryMegabytes,
            ThermalLimitCelsius = ThermalLimit,
            StopOnError = StopOnError,
        };

        var progress = new Progress<StressTestProgress>(p =>
        {
            Progress = p.Fraction * 100;
            Elapsed = p.Elapsed.ToString(@"hh\:mm\:ss");
            Iterations = p.Iterations.ToString("N0", Loc.Instance.Culture);
            Rate = p.IterationsPerSecond.ToString("N1", Loc.Instance.Culture);
            Errors = p.Errors;
            if (p.MaxTemperature is { } mt) MaxTemp = SensorUnit.Celsius.FormatValue(mt, Loc.Instance.Culture);
        });

        try
        {
            var result = await _stress.RunAsync(options, progress, _cts.Token);
            Results.Insert(0, result);
            Status = result.Summary;
            LastOutcome = result.Outcome;
            if (result.MaxTemperature is { } max) MaxTemp = SensorUnit.Celsius.FormatValue(max, Loc.Instance.Culture);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            LastOutcome = StressTestOutcome.Error;
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void SelectKind(StressKindOption option) => SelectedKind = option;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();
}
