using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Alerts;

/// <summary>
/// Evaluates component-aware built-in rules and user rules against each snapshot.
/// Per (rule, sensor) it tracks warning / critical levels with sustain time, hysteresis and cooldown so that
/// brief spikes don't alert, an oscillating sensor doesn't spam, and escalation warning → critical is reported once.
/// </summary>
public sealed class AlertService : IAlertService
{
    private const int MaxHistory = 500;
    private const double FanSpinningRpm = 300;

    private readonly ILogger<AlertService> _logger;
    private readonly Lock _gate = new();
    private readonly List<AlertEvent> _events = [];
    private readonly Dictionary<(Guid Rule, string Sensor), Track> _state = [];
    private readonly Dictionary<string, (double MaxRpm, DateTimeOffset At)> _fanHistory = new(StringComparer.Ordinal);
    private List<AlertRule> _rules = [];
    private List<ComponentAlertRule> _componentRules = [.. ThresholdResolver.DefaultRules];

    public AlertService(ILogger<AlertService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<AlertEvent>? AlertRaised;

    public IReadOnlyList<AlertRule> Rules { get { lock (_gate) return _rules.ToList(); } }
    public IReadOnlyList<ComponentAlertRule> ComponentRules { get { lock (_gate) return _componentRules.ToList(); } }
    public IReadOnlyList<AlertEvent> RecentEvents { get { lock (_gate) return _events.ToList(); } }

    public int ActiveAlertCount { get { lock (_gate) return _state.Values.Count(t => t.Level != AlertSeverity.Information); } }

    public void SetRules(IEnumerable<AlertRule> rules)
    {
        lock (_gate)
        {
            _rules = rules.ToList();
            Prune();
        }
    }

    public void SetComponentRules(IEnumerable<ComponentAlertRule> rules)
    {
        lock (_gate)
        {
            var given = rules.ToDictionary(r => r.Role);
            _componentRules = ThresholdResolver.DefaultRules.Select(d => given.GetValueOrDefault(d.Role, d)).ToList();
            Prune();
        }
    }

    private void Prune()
    {
        var ids = _rules.Select(r => r.Id).Concat(_componentRules.Where(r => r.Enabled).Select(r => r.Id)).ToHashSet();
        foreach (var key in _state.Keys.Where(k => !ids.Contains(k.Rule)).ToList()) _state.Remove(key);
    }

    public IReadOnlyList<AlertEvent> Evaluate(SensorSnapshot snapshot)
    {
        var raised = new List<AlertEvent>();
        var now = snapshot.Timestamp;
        var byDevice = snapshot.Sensors.GroupBy(s => s.DeviceId).ToDictionary(g => g.Key, g => (IReadOnlyList<SensorReading>)g.ToList(), StringComparer.Ordinal);

        lock (_gate)
        {
            TrackFans(snapshot, now);
            foreach (var rule in _componentRules.Where(r => r.Enabled))
            {
                foreach (var device in snapshot.Devices)
                {
                    if (!byDevice.TryGetValue(device.DeviceId, out var sensors)) continue;
                    foreach (var target in ThresholdResolver.Targets(rule.Role, device, sensors))
                    {
                        if (target.Value is not { } value || target.Quality is DataQuality.Missing or DataQuality.Suspect or DataQuality.Stale) continue;
                        EvaluateComponent(rule, device, sensors, target, value, now, raised, byDevice);
                    }
                }
            }

            var devices = snapshot.Devices.ToDictionary(d => d.DeviceId, StringComparer.Ordinal);
            foreach (var rule in _rules.Where(r => r.Enabled))
            {
                foreach (var sensor in snapshot.Sensors)
                {
                    if (!sensor.IsMeasurement || sensor.Value is not { } value || sensor.Quality is DataQuality.Missing or DataQuality.Suspect or DataQuality.Stale) continue;
                    devices.TryGetValue(sensor.DeviceId, out var device);
                    if (!Matches(rule, sensor, device)) continue;
                    var above = rule.Comparison == AlertComparison.Above;
                    var level = new ThresholdLevel(rule.Threshold, AlertKind.UserThreshold, "your threshold");
                    Step(rule.Id, rule.Name, sensor, device, value, now, rule.Sustain, rule.Hysteresis, rule.Cooldown, above,
                        rule.Severity == AlertSeverity.Critical ? null : level, rule.Severity == AlertSeverity.Critical ? level : null, raised);
                }
            }
        }

        foreach (var e in raised)
        {
            if (e.State == AlertState.Raised) _logger.LogWarning("Alert {Kind}: {Message}", e.Kind, e.Message);
            AlertRaised?.Invoke(this, e);
        }

        return raised;
    }

    private void EvaluateComponent(ComponentAlertRule rule, HardwareDevice device, IReadOnlyList<SensorReading> sensors, SensorReading target, double value,
        DateTimeOffset now, List<AlertEvent> raised, Dictionary<string, IReadOnlyList<SensorReading>> byDevice)
    {
        var name = RuleName(rule.Role);
        switch (rule.Role)
        {
            case ComponentRole.ThermalThrottling:
                Step(rule.Id, name, target, device, value, now, rule.Sustain, 0.5, rule.Cooldown, true,
                    new ThresholdLevel(0.5, AlertKind.Throttling, "reported by the hardware"), null, raised);
                return;

            case ComponentRole.FanFailure:
            {
                // A GPU fan at 0 RPM is normal (zero-RPM mode) unless the GPU is warm.
                if (device.Category == HardwareCategory.Gpu)
                {
                    var gpuTemp = sensors.Where(s => s.IsMeasurement && s.Type == SensorType.Temperature && s.Value.HasValue).Select(s => s.Value!.Value).DefaultIfEmpty(0).Max();
                    if (gpuTemp < 60) return;
                }

                var spunBefore = _fanHistory.TryGetValue(target.SensorId, out var h) && h.MaxRpm >= FanSpinningRpm;
                if (!spunBefore) return;
                // Condition "below 1 RPM" expressed as an inverted threshold.
                Step(rule.Id, name, target, device, value, now, rule.Sustain, FanSpinningRpm - 1, rule.Cooldown, false,
                    new ThresholdLevel(1, AlertKind.FanFailure, "fan stopped"), null, raised);
                return;
            }

            default:
            {
                var t = ThresholdResolver.Resolve(rule, device, sensors, target);
                if (t.Warning is null && t.Critical is null) return;
                Step(rule.Id, name, target, device, value, now, rule.Sustain, rule.Hysteresis, rule.Cooldown, true, t.Warning, t.Critical, raised);
                return;
            }
        }
    }

    private void TrackFans(SensorSnapshot snapshot, DateTimeOffset now)
    {
        foreach (var s in snapshot.Sensors)
        {
            if (s.Type != SensorType.FanSpeed || s.Value is not { } rpm) continue;
            if (!_fanHistory.TryGetValue(s.SensorId, out var h) || rpm >= h.MaxRpm || now - h.At > TimeSpan.FromMinutes(10))
            {
                if (rpm >= FanSpinningRpm || !_fanHistory.ContainsKey(s.SensorId)) _fanHistory[s.SensorId] = (rpm, now);
            }
        }
    }

    /// <summary>Two-level state machine with sustain, hysteresis and cooldown.</summary>
    private void Step(Guid ruleId, string ruleName, SensorReading sensor, HardwareDevice? device, double value, DateTimeOffset now,
        TimeSpan sustain, double hysteresis, TimeSpan cooldown, bool above, ThresholdLevel? warning, ThresholdLevel? critical, List<AlertEvent> raised)
    {
        var key = (ruleId, sensor.SensorId);
        if (!_state.TryGetValue(key, out var st)) _state[key] = st = new Track();

        bool Beyond(ThresholdLevel? l) => l is not null && (above ? value >= l.Value : value <= l.Value);
        var target = Beyond(critical) ? AlertSeverity.Critical : Beyond(warning) ? AlertSeverity.Warning : AlertSeverity.Information;

        if (target > st.Level)
        {
            st.BreachSince ??= now;
            if (st.PendingLevel != target)
            {
                st.PendingLevel = target;
                // Escalating from an active warning to critical does not restart the sustain timer.
                if (st.Level == AlertSeverity.Information) st.BreachSince = now;
            }

            if (now - st.BreachSince.Value < sustain) return;
            var level = target == AlertSeverity.Critical ? critical! : warning!;
            st.Level = target;
            st.ActiveThreshold = level;
            if (st.LastRaised.TryGetValue(target, out var last) && now - last < cooldown) return; // cooldown: stay active, stay quiet
            st.LastRaised[target] = now;
            st.Notified = true;
            raised.Add(Record(ruleId, ruleName, sensor, device, value, now, target, AlertState.Raised, level));
            return;
        }

        if (target == st.Level)
        {
            if (target == AlertSeverity.Information) { st.BreachSince = null; st.PendingLevel = AlertSeverity.Information; }
            return;
        }

        // Value dropped below the active level: clear only once it is past the hysteresis band of the lowest active threshold.
        st.PendingLevel = target;
        var clearAt = (warning ?? critical)!.Value;
        var cleared = above ? value <= clearAt - hysteresis : value >= clearAt + hysteresis;
        if (target == AlertSeverity.Warning && st.Level == AlertSeverity.Critical)
        {
            var critClear = above ? value <= critical!.Value - hysteresis : value >= critical!.Value + hysteresis;
            if (critClear) { st.Level = AlertSeverity.Warning; st.ActiveThreshold = warning; }
            return;
        }

        if (!cleared) return;
        var wasNotified = st.Notified;
        var previous = st.ActiveThreshold;
        st.Level = AlertSeverity.Information;
        st.BreachSince = null;
        st.Notified = false;
        if (wasNotified && previous is not null)
        {
            raised.Add(Record(ruleId, ruleName, sensor, device, value, now, AlertSeverity.Information, AlertState.Cleared, previous));
        }
    }

    private AlertEvent Record(Guid ruleId, string ruleName, SensorReading sensor, HardwareDevice? device, double value, DateTimeOffset ts,
        AlertSeverity severity, AlertState state, ThresholdLevel level)
    {
        var e = new AlertEvent(ruleId, ruleName, severity, state, level.Kind, sensor.SensorId, sensor.Name,
            device?.Name ?? sensor.DeviceId, value, level.Value, sensor.Unit, ts, level.Source);
        _events.Insert(0, e);
        if (_events.Count > MaxHistory) _events.RemoveRange(MaxHistory, _events.Count - MaxHistory);
        return e;
    }

    public static string RuleName(ComponentRole role) => role switch
    {
        ComponentRole.CpuTemperature => "CPU temperature",
        ComponentRole.GpuTemperature => "GPU temperature",
        ComponentRole.GpuHotspot => "GPU hotspot",
        ComponentRole.GpuMemoryTemperature => "GPU memory temperature",
        ComponentRole.StorageTemperature => "Drive temperature",
        ComponentRole.MemoryUtilization => "Memory utilization",
        ComponentRole.ThermalThrottling => "Thermal throttling",
        ComponentRole.FanFailure => "Fan failure",
        _ => role.ToString(),
    };

    internal static bool Matches(AlertRule rule, SensorReading sensor, HardwareDevice? device)
    {
        if (rule.SensorId is not null) return string.Equals(rule.SensorId, sensor.SensorId, StringComparison.Ordinal);
        if (rule.SensorType is { } t && sensor.Type != t) return false;
        if (rule.DeviceCategory is { } c && device?.Category != c) return false;
        if (!string.IsNullOrWhiteSpace(rule.NameContains) && !sensor.Name.Contains(rule.NameContains, StringComparison.OrdinalIgnoreCase)) return false;
        return rule.SensorType is not null || rule.DeviceCategory is not null || !string.IsNullOrWhiteSpace(rule.NameContains);
    }

    public void ClearHistory()
    {
        lock (_gate) _events.Clear();
    }

    private sealed class Track
    {
        public AlertSeverity Level { get; set; } = AlertSeverity.Information;
        public AlertSeverity PendingLevel { get; set; } = AlertSeverity.Information;
        public DateTimeOffset? BreachSince { get; set; }
        public ThresholdLevel? ActiveThreshold { get; set; }
        public bool Notified { get; set; }
        public Dictionary<AlertSeverity, DateTimeOffset> LastRaised { get; } = [];
    }
}

/// <summary>Validation of user-entered alert thresholds.</summary>
public static class AlertRuleValidator
{
    public static IReadOnlyList<string> Validate(ComponentAlertRule rule)
    {
        var errors = new List<string>();
        if (!rule.UseAutomaticThresholds)
        {
            if (rule.WarningThreshold is null && rule.CriticalThreshold is null) errors.Add("Set a warning or a critical threshold, or use automatic thresholds.");
            if (rule.WarningThreshold is { } w && rule.CriticalThreshold is { } c && w >= c) errors.Add("The warning threshold must be below the critical threshold.");
            foreach (var v in new[] { rule.WarningThreshold, rule.CriticalThreshold }.OfType<double>())
            {
                if (!double.IsFinite(v) || v < -50 || v > 200) errors.Add("Thresholds must be between −50 and 200.");
            }
        }

        if (rule.Hysteresis < 0 || rule.Hysteresis > 50) errors.Add("Hysteresis must be between 0 and 50.");
        if (rule.Sustain < TimeSpan.Zero || rule.Sustain > TimeSpan.FromMinutes(10)) errors.Add("Sustain must be between 0 s and 10 min.");
        if (rule.Cooldown < TimeSpan.Zero || rule.Cooldown > TimeSpan.FromHours(24)) errors.Add("Cooldown must be between 0 and 24 h.");
        return errors.Distinct().ToList();
    }

    public static IReadOnlyList<string> Validate(AlertRule rule)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rule.Name)) errors.Add("A rule needs a name.");
        if (rule.SensorId is null && rule.SensorType is null && rule.DeviceCategory is null && string.IsNullOrWhiteSpace(rule.NameContains))
            errors.Add("A rule needs a target: a sensor type, a device category or a name filter.");
        if (!double.IsFinite(rule.Threshold)) errors.Add("The threshold must be a number.");
        if (rule.Hysteresis < 0) errors.Add("Hysteresis cannot be negative.");
        if (rule.Cooldown < TimeSpan.Zero) errors.Add("Cooldown cannot be negative.");
        return errors;
    }
}
