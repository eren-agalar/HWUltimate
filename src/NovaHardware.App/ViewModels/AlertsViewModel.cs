using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Alerts;
using NovaHardware.Core.Settings;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.App.ViewModels;

/// <summary>Editable projection of an <see cref="AlertRule"/>.</summary>
public sealed partial class AlertRuleRow : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? SensorId { get; init; }
    public string? NameContains { get; init; }
    [ObservableProperty] private string _name = "New rule";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private SensorType? _sensorType = Domain.Sensors.SensorType.Temperature;
    [ObservableProperty] private HardwareCategory? _deviceCategory = HardwareCategory.Cpu;
    [ObservableProperty] private AlertComparison _comparison;
    [ObservableProperty] private double _threshold = 90;
    [ObservableProperty] private double _sustainSeconds = 5;
    [ObservableProperty] private double _hysteresis = 2;
    [ObservableProperty] private AlertSeverity _severity = AlertSeverity.Warning;

    public static AlertRuleRow From(AlertRule r) => new()
    {
        Id = r.Id, SensorId = r.SensorId, NameContains = r.NameContains, Name = r.Name, Enabled = r.Enabled, SensorType = r.SensorType,
        DeviceCategory = r.DeviceCategory, Comparison = r.Comparison, Threshold = r.Threshold,
        SustainSeconds = r.Sustain.TotalSeconds, Hysteresis = r.Hysteresis, Severity = r.Severity,
    };

    public AlertRule ToRule() => new()
    {
        Id = Id, SensorId = SensorId, NameContains = NameContains, Name = Name, Enabled = Enabled, SensorType = SensorType,
        DeviceCategory = DeviceCategory, Comparison = Comparison, Threshold = Threshold,
        Sustain = TimeSpan.FromSeconds(Math.Max(0, SustainSeconds)), Hysteresis = Math.Max(0, Hysteresis), Severity = Severity,
    };
}

public sealed partial class AlertsViewModel : ObservableObject
{
    private readonly IAlertService _alerts;
    private readonly ISettingsService _settings;

    public AlertsViewModel(IAlertService alerts, ISettingsService settings)
    {
        _alerts = alerts;
        _settings = settings;
        _alerts.AlertRaised += (_, e) => Ui.Post(() =>
        {
            Events.Insert(0, e);
            while (Events.Count > 500) Events.RemoveAt(Events.Count - 1);
        });
    }

    public ObservableCollection<AlertRuleRow> Rules { get; } = [];
    public ObservableCollection<AlertEvent> Events { get; } = [];

    // Static so DataGrid columns (which are not in the visual tree) can bind via x:Static.
    public static IReadOnlyList<SensorType?> SensorTypes { get; } = Enum.GetValues<SensorType>().Select(t => (SensorType?)t).ToList();
    public static IReadOnlyList<HardwareCategory?> Categories { get; } = [null, .. Enum.GetValues<HardwareCategory>().Select(c => (HardwareCategory?)c)];
    public static IReadOnlyList<AlertComparison> Comparisons { get; } = Enum.GetValues<AlertComparison>();
    public static IReadOnlyList<AlertSeverity> Severities { get; } = Enum.GetValues<AlertSeverity>();

    [ObservableProperty] private AlertRuleRow? _selectedRule;
    [ObservableProperty] private string _message = string.Empty;

    public void Load()
    {
        Rules.Clear();
        foreach (var r in _alerts.Rules) Rules.Add(AlertRuleRow.From(r));
        Events.Clear();
        foreach (var e in _alerts.RecentEvents) Events.Add(e);
    }

    [RelayCommand]
    private void AddRule()
    {
        var row = new AlertRuleRow();
        Rules.Add(row);
        SelectedRule = row;
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is not null) Rules.Remove(SelectedRule);
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        Rules.Clear();
        foreach (var r in AlertService.DefaultRules) Rules.Add(AlertRuleRow.From(r with { Id = Guid.NewGuid() }));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var rules = Rules.Select(r => r.ToRule()).ToList();
        _alerts.SetRules(rules);
        await _settings.SaveAsync(_settings.Current with { AlertRules = rules });
        Message = UI.Localization.Loc.Get("Settings_Saved");
    }

    [RelayCommand]
    private void ClearEvents()
    {
        _alerts.ClearHistory();
        Events.Clear();
    }
}
