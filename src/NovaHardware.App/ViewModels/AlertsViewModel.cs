using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Alerts;
using NovaHardware.Core.Settings;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

/// <summary>Editable component rule with a description of the thresholds currently in effect.</summary>
public sealed partial class ComponentRuleRow : ObservableObject
{
    public ComponentRuleRow(ComponentAlertRule rule)
    {
        Role = rule.Role;
        _enabled = rule.Enabled;
        _useAutomatic = rule.UseAutomaticThresholds;
        _warning = rule.WarningThreshold?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        _critical = rule.CriticalThreshold?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        _hysteresis = rule.Hysteresis;
        _sustainSeconds = rule.Sustain.TotalSeconds;
        _cooldownMinutes = rule.Cooldown.TotalMinutes;
    }

    public ComponentRole Role { get; }
    public string Title => Loc.Get($"Rule_{Role}");
    public string Description => Loc.Get($"Rule_{Role}_Desc");
    public bool HasThresholds => Role is not (ComponentRole.ThermalThrottling or ComponentRole.FanFailure);

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _useAutomatic;
    [ObservableProperty] private string _warning;
    [ObservableProperty] private string _critical;
    [ObservableProperty] private double _hysteresis;
    [ObservableProperty] private double _sustainSeconds;
    [ObservableProperty] private double _cooldownMinutes;
    [ObservableProperty] private string _effective = string.Empty;

    private static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : null;

    public ComponentAlertRule ToRule() => new()
    {
        Role = Role,
        Enabled = Enabled,
        UseAutomaticThresholds = UseAutomatic,
        WarningThreshold = Num(Warning),
        CriticalThreshold = Num(Critical),
        Hysteresis = Hysteresis,
        Sustain = TimeSpan.FromSeconds(Math.Max(0, SustainSeconds)),
        Cooldown = TimeSpan.FromMinutes(Math.Max(0, CooldownMinutes)),
    };
}

public sealed partial class CustomRuleRow : ObservableObject
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
    [ObservableProperty] private AlertSeverity _severity = AlertSeverity.Warning;

    public static CustomRuleRow From(AlertRule r) => new()
    {
        Id = r.Id, SensorId = r.SensorId, NameContains = r.NameContains, Name = r.Name, Enabled = r.Enabled, SensorType = r.SensorType,
        DeviceCategory = r.DeviceCategory, Comparison = r.Comparison, Threshold = r.Threshold, Severity = r.Severity,
    };

    public AlertRule ToRule(double hysteresis, TimeSpan cooldown) => new()
    {
        Id = Id, SensorId = SensorId, NameContains = NameContains, Name = Name, Enabled = Enabled, SensorType = SensorType,
        DeviceCategory = DeviceCategory, Comparison = Comparison, Threshold = Threshold, Severity = Severity, Hysteresis = hysteresis, Cooldown = cooldown,
    };
}

public sealed record AlertEventRow(AlertEvent Event)
{
    public string Time => Event.Timestamp.LocalDateTime.ToString("G", Loc.Instance.Culture);
    public string Severity => Loc.Get($"Severity_{Event.Severity}");
    public string Kind => Loc.Get($"AlertKind_{Event.Kind}");
    public string Message => Event.Message;
    public bool IsCleared => Event.State == AlertState.Cleared;
    public AlertSeverity Level => Event.Severity;
}

public sealed partial class AlertsViewModel : ObservableObject
{
    private readonly IAlertService _alerts;
    private readonly ISettingsService _settings;
    private readonly ISensorPollingService _polling;

    public AlertsViewModel(IAlertService alerts, ISettingsService settings, ISensorPollingService polling)
    {
        _alerts = alerts;
        _settings = settings;
        _polling = polling;
        _alerts.AlertRaised += (_, e) => Ui.Post(() =>
        {
            Events.Insert(0, new AlertEventRow(e));
            while (Events.Count > 500) Events.RemoveAt(Events.Count - 1);
        });
        _notificationsEnabled = settings.Current.Alerts.NotificationsEnabled;
    }

    public ObservableCollection<ComponentRuleRow> ComponentRules { get; } = [];
    public ObservableCollection<CustomRuleRow> CustomRules { get; } = [];
    public ObservableCollection<AlertEventRow> Events { get; } = [];

    public static IReadOnlyList<SensorType?> SensorTypes { get; } = Enum.GetValues<SensorType>().Select(t => (SensorType?)t).ToList();
    public static IReadOnlyList<HardwareCategory?> Categories { get; } = [null, .. Enum.GetValues<HardwareCategory>().Select(c => (HardwareCategory?)c)];
    public static IReadOnlyList<AlertComparison> Comparisons { get; } = Enum.GetValues<AlertComparison>();
    public static IReadOnlyList<AlertSeverity> Severities { get; } = Enum.GetValues<AlertSeverity>();

    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private CustomRuleRow? _selectedCustomRule;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _hasErrors;

    public void Load()
    {
        ComponentRules.Clear();
        foreach (var r in _alerts.ComponentRules) ComponentRules.Add(new ComponentRuleRow(r));
        CustomRules.Clear();
        foreach (var r in _alerts.Rules) CustomRules.Add(CustomRuleRow.From(r));
        Events.Clear();
        foreach (var e in _alerts.RecentEvents) Events.Add(new AlertEventRow(e));
        DescribeEffective();
    }

    /// <summary>Shows, per rule, the thresholds actually resolved for the detected hardware and where they come from.</summary>
    private void DescribeEffective()
    {
        var snap = _polling.Current;
        var byDevice = snap.Sensors.GroupBy(s => s.DeviceId).ToDictionary(g => g.Key, g => (IReadOnlyList<SensorReading>)g.ToList(), StringComparer.Ordinal);
        foreach (var row in ComponentRules)
        {
            if (!row.HasThresholds) { row.Effective = Loc.Get("Rule_Detected"); continue; }
            var rule = row.ToRule();
            var parts = new List<string>();
            foreach (var device in snap.Devices)
            {
                if (!byDevice.TryGetValue(device.DeviceId, out var sensors)) continue;
                foreach (var target in ThresholdResolver.Targets(rule.Role, device, sensors))
                {
                    var t = ThresholdResolver.Resolve(rule, device, sensors, target);
                    var c = Loc.Instance.Culture;
                    parts.Add($"{device.Name}: {target.Unit.FormatValue(t.Warning?.Value, c, "—")} / {target.Unit.FormatValue(t.Critical?.Value, c, "—")} ({t.Critical?.Source ?? t.Warning?.Source})");
                }
            }

            row.Effective = parts.Count == 0 ? Loc.Get("Rule_NoSensor") : string.Join("\n", parts.Distinct());
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        var row = new CustomRuleRow { Name = Loc.Get("Alerts_NewRule") };
        CustomRules.Add(row);
        SelectedCustomRule = row;
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedCustomRule is not null) CustomRules.Remove(SelectedCustomRule);
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        ComponentRules.Clear();
        foreach (var r in ThresholdResolver.DefaultRules) ComponentRules.Add(new ComponentRuleRow(r));
        DescribeEffective();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var component = ComponentRules.Select(r => r.ToRule()).ToList();
        var custom = CustomRules.Select(r => r.ToRule(2, TimeSpan.FromMinutes(5))).ToList();
        var updated = _settings.Current with { Alerts = _settings.Current.Alerts with { NotificationsEnabled = NotificationsEnabled, ComponentRules = component, CustomRules = custom } };
        var errors = SettingsValidator.Validate(updated);
        HasErrors = errors.Count > 0;
        if (HasErrors)
        {
            Message = string.Join("\n", errors);
            return;
        }

        _alerts.SetComponentRules(component);
        _alerts.SetRules(custom);
        await _settings.SaveAsync(updated);
        DescribeEffective();
        Message = Loc.Get("Settings_Saved");
    }

    [RelayCommand]
    private void ClearEvents()
    {
        _alerts.ClearHistory();
        Events.Clear();
    }
}
