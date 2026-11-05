using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Alerts;

/// <summary>
/// Evaluates <see cref="AlertRule"/>s against each snapshot with sustain time and hysteresis so
/// that brief spikes and noisy sensors do not spam the user.
/// </summary>
public sealed class AlertService : IAlertService
{
    private const int MaxHistory = 500;

    private readonly ILogger<AlertService> _logger;
    private readonly Lock _gate = new();
    private readonly List<AlertEvent> _events = [];
    private readonly Dictionary<(Guid Rule, string Sensor), TrackState> _state = [];
    private List<AlertRule> _rules = [];

    public AlertService(ILogger<AlertService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<AlertEvent>? AlertRaised;

    public IReadOnlyList<AlertRule> Rules { get { lock (_gate) return _rules.ToList(); } }

    public IReadOnlyList<AlertEvent> RecentEvents { get { lock (_gate) return _events.ToList(); } }

    public IReadOnlyCollection<Guid> ActiveRuleIds
    {
        get { lock (_gate) return _state.Where(kv => kv.Value.Active).Select(kv => kv.Key.Rule).Distinct().ToList(); }
    }

    public static IReadOnlyList<AlertRule> DefaultRules { get; } =
    [
        new() { Name = "CPU temperature high", SensorType = SensorType.Temperature, DeviceCategory = HardwareCategory.Cpu, Threshold = 90, Severity = AlertSeverity.Warning },
        new() { Name = "CPU temperature critical", SensorType = SensorType.Temperature, DeviceCategory = HardwareCategory.Cpu, Threshold = 100, Severity = AlertSeverity.Critical, Sustain = TimeSpan.FromSeconds(2) },
        new() { Name = "GPU temperature high", SensorType = SensorType.Temperature, DeviceCategory = HardwareCategory.Gpu, Threshold = 85, Severity = AlertSeverity.Warning },
        new() { Name = "Drive temperature high", SensorType = SensorType.Temperature, DeviceCategory = HardwareCategory.Storage, Threshold = 65, Severity = AlertSeverity.Warning, Sustain = TimeSpan.FromSeconds(30) },
        new() { Name = "Memory usage high", SensorType = SensorType.Utilization, DeviceCategory = HardwareCategory.Memory, Threshold = 95, Severity = AlertSeverity.Information, Sustain = TimeSpan.FromSeconds(30), Hysteresis = 5 },
    ];

    public void SetRules(IEnumerable<AlertRule> rules)
    {
        lock (_gate)
        {
            _rules = rules.ToList();
            var ids = _rules.Select(r => r.Id).ToHashSet();
            foreach (var key in _state.Keys.Where(k => !ids.Contains(k.Rule)).ToList())
            {
                _state.Remove(key);
            }
        }
    }

    public IReadOnlyList<AlertEvent> Evaluate(SensorSnapshot snapshot)
    {
        var raised = new List<AlertEvent>();
        var devices = snapshot.Devices.ToDictionary(d => d.DeviceId, StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var rule in _rules.Where(r => r.Enabled))
            {
                foreach (var sensor in snapshot.Sensors)
                {
                    if (sensor.Value is not { } value || sensor.Quality is DataQuality.Missing or DataQuality.Suspect)
                    {
                        continue;
                    }

                    devices.TryGetValue(sensor.DeviceId, out var device);
                    if (!Matches(rule, sensor, device))
                    {
                        continue;
                    }

                    var key = (rule.Id, sensor.SensorId);
                    _state.TryGetValue(key, out var st);
                    st ??= new TrackState();
                    _state[key] = st;

                    var breaching = rule.Comparison == AlertComparison.Above ? value > rule.Threshold : value < rule.Threshold;
                    var recovered = rule.Comparison == AlertComparison.Above
                        ? value <= rule.Threshold - rule.Hysteresis
                        : value >= rule.Threshold + rule.Hysteresis;

                    if (!st.Active)
                    {
                        if (breaching)
                        {
                            st.BreachSince ??= snapshot.Timestamp;
                            if (snapshot.Timestamp - st.BreachSince.Value >= rule.Sustain)
                            {
                                st.Active = true;
                                raised.Add(Record(rule, sensor, device, value, AlertState.Raised, snapshot.Timestamp));
                            }
                        }
                        else
                        {
                            st.BreachSince = null;
                        }
                    }
                    else if (recovered)
                    {
                        st.Active = false;
                        st.BreachSince = null;
                        raised.Add(Record(rule, sensor, device, value, AlertState.Cleared, snapshot.Timestamp));
                    }
                }
            }
        }

        foreach (var e in raised)
        {
            if (e.State == AlertState.Raised)
            {
                _logger.LogWarning("Alert {Rule}: {Message}", e.RuleName, e.Message);
            }

            AlertRaised?.Invoke(this, e);
        }

        return raised;
    }

    private AlertEvent Record(AlertRule rule, SensorReading sensor, HardwareDevice? device, double value, AlertState state, DateTimeOffset ts)
    {
        var e = new AlertEvent(rule.Id, rule.Name, rule.Severity, state, sensor.SensorId, sensor.Name,
            device?.Name ?? sensor.DeviceId, value, rule.Threshold, sensor.Unit, ts);
        _events.Insert(0, e);
        if (_events.Count > MaxHistory)
        {
            _events.RemoveRange(MaxHistory, _events.Count - MaxHistory);
        }

        return e;
    }

    internal static bool Matches(AlertRule rule, SensorReading sensor, HardwareDevice? device)
    {
        if (rule.SensorId is not null)
        {
            return string.Equals(rule.SensorId, sensor.SensorId, StringComparison.Ordinal);
        }

        if (rule.SensorType is { } t && sensor.Type != t)
        {
            return false;
        }

        if (rule.DeviceCategory is { } c && device?.Category != c)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.NameContains) && !sensor.Name.Contains(rule.NameContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A rule needs at least one targeting criterion to avoid matching every sensor.
        return rule.SensorType is not null || rule.DeviceCategory is not null || !string.IsNullOrWhiteSpace(rule.NameContains);
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }

    private sealed class TrackState
    {
        public DateTimeOffset? BreachSince { get; set; }
        public bool Active { get; set; }
    }
}
