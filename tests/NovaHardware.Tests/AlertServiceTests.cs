using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.Core.Alerts;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Tests;

public class AlertServiceTests
{
    private static readonly HardwareDevice AmdCpu = new("cpu", "AMD Ryzen 9 7950X", HardwareCategory.Cpu, "t", Vendor: "AMD");
    private static readonly HardwareDevice IntelCpu = new("cpu", "Intel Core i9-14900K", HardwareCategory.Cpu, "t", Vendor: "Intel");
    private static readonly HardwareDevice NvGpu = new("gpu", "NVIDIA GeForce RTX 4090", HardwareCategory.Gpu, "t", Vendor: "NVIDIA");
    private static readonly HardwareDevice Nvme = new("ssd", "Samsung SSD 990 PRO", HardwareCategory.Storage, "t");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AlertService Create(params ComponentAlertRule[] overrides)
    {
        var s = new AlertService(NullLogger<AlertService>.Instance);
        // Only the roles under test are enabled.
        var roles = overrides.Select(o => o.Role).ToHashSet();
        s.SetComponentRules(ThresholdResolver.DefaultRules.Select(d => roles.Contains(d.Role) ? overrides.First(o => o.Role == d.Role) : d with { Enabled = false }));
        return s;
    }

    private static ComponentAlertRule Rule(ComponentRole role) => ThresholdResolver.DefaultRules.First(r => r.Role == role) with { Sustain = TimeSpan.Zero };

    private static SensorSnapshot Snap(double seconds, HardwareDevice device, params SensorReading[] sensors) =>
        Snapshots.Of(T0.AddSeconds(seconds), [device], sensors);

    private static SensorReading Temp(string name, double? v, string id = "t", string device = "cpu", SensorKind kind = SensorKind.Measurement) =>
        Snapshots.Reading(id, device, name, SensorType.Temperature, v, kind: kind);

    [Fact]
    public void Uses_vendor_specific_cpu_defaults()
    {
        var amd = ThresholdResolver.Resolve(Rule(ComponentRole.CpuTemperature), AmdCpu, [], Temp("CPU Package", 70));
        var intel = ThresholdResolver.Resolve(Rule(ComponentRole.CpuTemperature), IntelCpu, [], Temp("CPU Package", 70));
        Assert.Equal((85d, 95d), (amd.Warning!.Value, amd.Critical!.Value));
        Assert.Equal((90d, 100d), (intel.Warning!.Value, intel.Critical!.Value));
        Assert.NotEqual(amd.Critical.Source, intel.Critical.Source);
    }

    [Fact]
    public void Device_reported_limits_take_precedence()
    {
        var cpuSensors = new[] { Temp("Core #1", 60, "c1"), Temp("Core #1 Distance to TjMax", 40, "d1") };
        var cpu = ThresholdResolver.Resolve(Rule(ComponentRole.CpuTemperature), IntelCpu, cpuSensors, cpuSensors[0]);
        Assert.Equal(100, cpu.Critical!.Value);
        Assert.Equal(AlertKind.DeviceReportedLimit, cpu.Critical.Kind);
        Assert.Equal(90, cpu.Warning!.Value);

        var gpuSensors = new[] { Temp("GPU Core", 60, "g", "gpu"), Temp("GPU Slowdown Temperature Threshold", 87, "s", "gpu", SensorKind.Metadata) };
        var gpu = ThresholdResolver.Resolve(Rule(ComponentRole.GpuTemperature), NvGpu, gpuSensors, gpuSensors[0]);
        Assert.Equal((82d, 87d), (gpu.Warning!.Value, gpu.Critical!.Value));
        Assert.Equal(AlertKind.DeviceReportedLimit, gpu.Critical.Kind);
    }

    [Fact]
    public void Smart_warning_and_critical_temperatures_are_not_interchanged()
    {
        var sensors = new[]
        {
            Temp("Composite Temperature", 50, "c", "ssd"),
            Temp("Warning Composite Temperature", 82, "w", "ssd", SensorKind.Metadata),
            Temp("Critical Composite Temperature", 85, "x", "ssd", SensorKind.Metadata),
        };
        var t = ThresholdResolver.Resolve(Rule(ComponentRole.StorageTemperature), Nvme, sensors, sensors[0]);
        Assert.Equal(82, t.Warning!.Value);
        Assert.Equal(85, t.Critical!.Value);
        Assert.Contains("warning", t.Warning.Source, StringComparison.Ordinal);
        Assert.Contains("critical", t.Critical.Source, StringComparison.Ordinal);

        // Only a critical limit reported → warning falls back to the class default, it is NOT taken from the critical value.
        var onlyCritical = ThresholdResolver.Resolve(Rule(ComponentRole.StorageTemperature), Nvme, [sensors[0], sensors[2]], sensors[0]);
        Assert.Equal(70, onlyCritical.Warning!.Value);
        Assert.Equal(85, onlyCritical.Critical!.Value);
    }

    [Fact]
    public void Metadata_sensors_are_never_alert_targets()
    {
        var sensors = new[] { Temp("Warning Composite Temperature", 82, "w", "ssd", SensorKind.Metadata) };
        Assert.Empty(ThresholdResolver.Targets(ComponentRole.StorageTemperature, Nvme, sensors));
    }

    [Fact]
    public void Distinguishes_warning_and_critical_and_escalates_once()
    {
        var s = Create(Rule(ComponentRole.CpuTemperature));
        Assert.Empty(s.Evaluate(Snap(0, AmdCpu, Temp("CPU Package", 80))));
        var warn = Assert.Single(s.Evaluate(Snap(1, AmdCpu, Temp("CPU Package", 88))));
        Assert.Equal((AlertSeverity.Warning, AlertKind.WarningThreshold), (warn.Severity, warn.Kind));
        Assert.Contains("warning threshold of 85", warn.Message, StringComparison.Ordinal);
        Assert.Empty(s.Evaluate(Snap(2, AmdCpu, Temp("CPU Package", 89)))); // no repeat
        var crit = Assert.Single(s.Evaluate(Snap(3, AmdCpu, Temp("CPU Package", 96))));
        Assert.Equal((AlertSeverity.Critical, AlertKind.CriticalThreshold), (crit.Severity, crit.Kind));
        Assert.Contains("critical threshold of 95", crit.Message, StringComparison.Ordinal);
        Assert.Empty(s.Evaluate(Snap(4, AmdCpu, Temp("CPU Package", 97))));
        Assert.Equal(1, s.ActiveAlertCount);
    }

    [Fact]
    public void Hysteresis_prevents_flapping_and_clear_is_reported_once()
    {
        var s = Create(Rule(ComponentRole.CpuTemperature) with { Hysteresis = 4 });
        s.Evaluate(Snap(0, AmdCpu, Temp("CPU Package", 86)));
        Assert.Empty(s.Evaluate(Snap(1, AmdCpu, Temp("CPU Package", 84)))); // below 85 but inside the 4° band
        Assert.Empty(s.Evaluate(Snap(2, AmdCpu, Temp("CPU Package", 86))));
        var clear = Assert.Single(s.Evaluate(Snap(3, AmdCpu, Temp("CPU Package", 80))));
        Assert.Equal(AlertState.Cleared, clear.State);
        Assert.Contains("returned to normal", clear.Message, StringComparison.Ordinal);
        Assert.Empty(s.Evaluate(Snap(4, AmdCpu, Temp("CPU Package", 79))));
    }

    [Fact]
    public void Cooldown_suppresses_repeated_identical_alerts()
    {
        var s = Create(Rule(ComponentRole.CpuTemperature) with { Hysteresis = 1, Cooldown = TimeSpan.FromMinutes(5) });
        Assert.Single(s.Evaluate(Snap(0, AmdCpu, Temp("CPU Package", 90))));
        Assert.Equal(AlertState.Cleared, Assert.Single(s.Evaluate(Snap(10, AmdCpu, Temp("CPU Package", 70)))).State);
        Assert.Empty(s.Evaluate(Snap(20, AmdCpu, Temp("CPU Package", 90)))); // re-breach within 5 min → no new alert
        Assert.Empty(s.Evaluate(Snap(30, AmdCpu, Temp("CPU Package", 70)))); // and no clear for an alert that was never shown
        Assert.Single(s.Evaluate(Snap(400, AmdCpu, Temp("CPU Package", 90)))); // after cooldown
    }

    [Fact]
    public void Sustain_requires_continuous_breach()
    {
        var s = Create(Rule(ComponentRole.CpuTemperature) with { Sustain = TimeSpan.FromSeconds(5) });
        Assert.Empty(s.Evaluate(Snap(0, AmdCpu, Temp("CPU Package", 90))));
        Assert.Empty(s.Evaluate(Snap(2, AmdCpu, Temp("CPU Package", 70))));
        Assert.Empty(s.Evaluate(Snap(4, AmdCpu, Temp("CPU Package", 90))));
        Assert.Empty(s.Evaluate(Snap(8, AmdCpu, Temp("CPU Package", 90))));
        Assert.Single(s.Evaluate(Snap(9, AmdCpu, Temp("CPU Package", 90))));
    }

    [Fact]
    public void User_thresholds_are_labelled_as_user_configured()
    {
        var s = Create(Rule(ComponentRole.GpuHotspot) with { UseAutomaticThresholds = false, WarningThreshold = 70, CriticalThreshold = 80 });
        var e = Assert.Single(s.Evaluate(Snap(0, NvGpu, Temp("GPU Hot Spot", 75, "h", "gpu"))));
        Assert.Equal(AlertKind.UserThreshold, e.Kind);
        Assert.Contains("your threshold of 70", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Thermal_throttling_flag_raises_throttling_alert()
    {
        var s = Create(Rule(ComponentRole.ThermalThrottling));
        var flag = Snapshots.Reading("f", "gpu", "Hardware Thermal Slowdown", SensorType.PerformanceLimit, 0);
        Assert.Empty(s.Evaluate(Snap(0, NvGpu, flag)));
        var e = Assert.Single(s.Evaluate(Snap(1, NvGpu, flag with { Value = 1 })));
        Assert.Equal(AlertKind.Throttling, e.Kind);
        Assert.Contains("hardware thermal slowdown", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fan_failure_only_after_fan_was_spinning_and_not_for_cool_gpu_zero_rpm()
    {
        var board = new HardwareDevice("sio", "Nuvoton", HardwareCategory.SuperIo, "t");
        var s = Create(Rule(ComponentRole.FanFailure));
        SensorReading Fan(double rpm, string dev = "sio") => Snapshots.Reading("fan", dev, "CPU Fan", SensorType.FanSpeed, rpm);
        Assert.Empty(s.Evaluate(Snap(0, board, Fan(0)))); // never spun: no alert
        s.Evaluate(Snap(1, board, Fan(1200)));
        var e = Assert.Single(s.Evaluate(Snap(2, board, Fan(0))));
        Assert.Equal(AlertKind.FanFailure, e.Kind);

        var gpu = Create(Rule(ComponentRole.FanFailure));
        gpu.Evaluate(Snapshots.Of(T0, [NvGpu], Fan(1500, "gpu"), Temp("GPU Core", 45, "g", "gpu")));
        Assert.Empty(gpu.Evaluate(Snapshots.Of(T0.AddSeconds(1), [NvGpu], Fan(0, "gpu"), Temp("GPU Core", 45, "g", "gpu"))));
    }

    [Fact]
    public void Missing_suspect_and_stale_values_are_ignored()
    {
        var s = Create(Rule(ComponentRole.CpuTemperature));
        Assert.Empty(s.Evaluate(Snap(0, AmdCpu, Temp("CPU Package", null))));
        Assert.Empty(s.Evaluate(Snap(1, AmdCpu, Snapshots.Reading("t", "cpu", "CPU Package", SensorType.Temperature, 250, DataQuality.Suspect))));
        Assert.Empty(s.Evaluate(Snap(2, AmdCpu, Snapshots.Reading("t", "cpu", "CPU Package", SensorType.Temperature, 99, DataQuality.Stale))));
    }

    [Fact]
    public void Custom_rules_still_work()
    {
        var s = Create();
        s.SetRules([new AlertRule { Name = "fan low", SensorId = "fan", Comparison = AlertComparison.Below, Threshold = 300, Sustain = TimeSpan.Zero }]);
        var e = Assert.Single(s.Evaluate(Snap(0, AmdCpu, Snapshots.Reading("fan", "cpu", "CPU Fan", SensorType.FanSpeed, 0))));
        Assert.Equal(AlertKind.UserThreshold, e.Kind);
        Assert.False(AlertService.Matches(new AlertRule { Name = "empty" }, Snapshots.Reading("x", "cpu", "x", SensorType.Power, 1), AmdCpu));
    }

    [Fact]
    public void Validates_thresholds()
    {
        Assert.Empty(AlertRuleValidator.Validate(Rule(ComponentRole.CpuTemperature)));
        Assert.Contains(AlertRuleValidator.Validate(Rule(ComponentRole.CpuTemperature) with { UseAutomaticThresholds = false, WarningThreshold = 95, CriticalThreshold = 90 }),
            e => e.Contains("below the critical", StringComparison.Ordinal));
        Assert.NotEmpty(AlertRuleValidator.Validate(Rule(ComponentRole.CpuTemperature) with { UseAutomaticThresholds = false }));
        Assert.NotEmpty(AlertRuleValidator.Validate(Rule(ComponentRole.CpuTemperature) with { Hysteresis = -1 }));
        Assert.NotEmpty(AlertRuleValidator.Validate(new AlertRule { Name = "", Threshold = double.NaN }));
        Assert.All(ThresholdResolver.DefaultRules, r => Assert.Empty(AlertRuleValidator.Validate(r)));
    }

    [Fact]
    public void Component_rule_ids_are_stable()
    {
        Assert.Equal(Rule(ComponentRole.GpuHotspot).Id, (ThresholdResolver.DefaultRules.First(r => r.Role == ComponentRole.GpuHotspot) with { Enabled = false }).Id);
        Assert.Equal(ThresholdResolver.DefaultRules.Count, ThresholdResolver.DefaultRules.Select(r => r.Id).Distinct().Count());
    }
}
