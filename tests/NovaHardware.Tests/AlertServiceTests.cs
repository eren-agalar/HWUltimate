using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.Core.Alerts;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Tests;

public class AlertServiceTests
{
    private static readonly HardwareDevice[] Devices = [new("cpu", "CPU", HardwareCategory.Cpu, "t"), new("gpu", "GPU", HardwareCategory.Gpu, "t")];
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AlertService Create(params AlertRule[] rules)
    {
        var s = new AlertService(NullLogger<AlertService>.Instance);
        s.SetRules(rules);
        return s;
    }

    private static SensorSnapshot Cpu(double? temp, double seconds, DataQuality q = DataQuality.Good) =>
        Snapshots.Of(T0.AddSeconds(seconds), Devices, Snapshots.Reading("t", "cpu", "CPU Package", SensorType.Temperature, temp, q));

    private static readonly AlertRule CpuHot = new()
    {
        Name = "hot", SensorType = SensorType.Temperature, DeviceCategory = HardwareCategory.Cpu,
        Threshold = 90, Hysteresis = 5, Sustain = TimeSpan.FromSeconds(3),
    };

    [Fact]
    public void Requires_sustained_breach_before_raising()
    {
        var s = Create(CpuHot);
        Assert.Empty(s.Evaluate(Cpu(95, 0)));
        Assert.Empty(s.Evaluate(Cpu(95, 2)));
        var e = Assert.Single(s.Evaluate(Cpu(95, 3)));
        Assert.Equal(AlertState.Raised, e.State);
        Assert.Contains(CpuHot.Id, s.ActiveRuleIds);
        Assert.Empty(s.Evaluate(Cpu(96, 4))); // no repeat while active
    }

    [Fact]
    public void Brief_spike_does_not_raise()
    {
        var s = Create(CpuHot);
        s.Evaluate(Cpu(95, 0));
        s.Evaluate(Cpu(80, 1));
        Assert.Empty(s.Evaluate(Cpu(95, 3.5)));
    }

    [Fact]
    public void Clears_only_after_hysteresis()
    {
        var s = Create(CpuHot with { Sustain = TimeSpan.Zero });
        s.Evaluate(Cpu(95, 0));
        Assert.Empty(s.Evaluate(Cpu(88, 1))); // below threshold but within hysteresis
        var e = Assert.Single(s.Evaluate(Cpu(84, 2)));
        Assert.Equal(AlertState.Cleared, e.State);
        Assert.Equal(2, s.RecentEvents.Count);
    }

    [Fact]
    public void Ignores_missing_and_suspect_values()
    {
        var s = Create(CpuHot with { Sustain = TimeSpan.Zero });
        Assert.Empty(s.Evaluate(Cpu(null, 0)));
        Assert.Empty(s.Evaluate(Cpu(250, 1, DataQuality.Suspect)));
    }

    [Fact]
    public void Category_filter_excludes_other_devices()
    {
        var s = Create(CpuHot with { Sustain = TimeSpan.Zero });
        var snap = Snapshots.Of(T0, Devices, Snapshots.Reading("g", "gpu", "GPU Core", SensorType.Temperature, 99));
        Assert.Empty(s.Evaluate(snap));
    }

    [Fact]
    public void Below_comparison_and_specific_sensor_id()
    {
        var rule = new AlertRule { Name = "fan stopped", SensorId = "fan", Comparison = AlertComparison.Below, Threshold = 300, Sustain = TimeSpan.Zero };
        var s = Create(rule);
        var snap = Snapshots.Of(T0, Devices, Snapshots.Reading("fan", "cpu", "CPU Fan", SensorType.FanSpeed, 0));
        Assert.Single(s.Evaluate(snap));
    }

    [Fact]
    public void Rule_without_any_target_matches_nothing()
    {
        var reading = Snapshots.Reading("x", "cpu", "x", SensorType.Power, 1);
        Assert.False(AlertService.Matches(new AlertRule { Name = "empty" }, reading, Devices[0]));
    }

    [Fact]
    public void Disabled_rule_is_not_evaluated()
    {
        var s = Create(CpuHot with { Enabled = false, Sustain = TimeSpan.Zero });
        Assert.Empty(s.Evaluate(Cpu(120, 0)));
    }

    [Fact]
    public void Default_rules_are_well_formed()
    {
        Assert.All(AlertService.DefaultRules, r => Assert.True(r.SensorType is not null && r.DeviceCategory is not null));
    }
}
