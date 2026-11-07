using Microsoft.Extensions.Logging.Abstractions;
using HWUltimate.Core.Sensors;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Tests;

public class SensorStatisticsTrackerTests
{
    [Fact]
    public void Tracks_min_max_average_and_ignores_missing_values()
    {
        var t = new SensorStatisticsTracker();
        t.Record("a", 10);
        t.Record("a", null);
        t.Record("a", double.NaN);
        t.Record("a", 30);
        var s = t.Record("a", 20);

        Assert.Equal(10, s.Minimum);
        Assert.Equal(30, s.Maximum);
        Assert.Equal(20, s.Average!.Value, 9);
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void Sensor_with_no_values_has_no_statistics()
    {
        var s = new SensorStatisticsTracker().Record("x", null);
        Assert.Null(s.Minimum);
        Assert.Null(s.Average);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var t = new SensorStatisticsTracker();
        t.Record("a", 5);
        t.Reset();
        Assert.Equal(0, t.Get("a").Count);
    }
}

public class SensorPollingServiceTests
{
    private static SensorPollingService Create(TimeProvider? time = null, params FakeProvider[] providers) =>
        new(providers, new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance, time);

    [Fact]
    public void Poll_normalizes_readings_and_preserves_units()
    {
        var p = new FakeProvider("lhm").WithDevice("cpu", HardwareCategory.Cpu)
            .Set("t", "cpu", "CPU Package", SensorType.Temperature, 55.5)
            .Set("v", "cpu", "Vcore", SensorType.Voltage, 1.25);
        var svc = Create(null, p);

        var snap = svc.PollOnce();

        var t = snap.Find("t")!;
        Assert.Equal(55.5, t.Value);
        Assert.Equal(SensorUnit.Celsius, t.Unit);
        Assert.Equal("°C", t.UnitSymbol);
        Assert.Equal(DataQuality.Good, t.Quality);
        Assert.Equal(SensorUnit.Volt, snap.Find("v")!.Unit);
        Assert.Single(snap.Devices);
    }

    [Fact]
    public void Missing_value_is_reported_as_missing_not_zero()
    {
        var p = new FakeProvider("lhm").WithDevice("cpu", HardwareCategory.Cpu)
            .Set("t", "cpu", "CPU Package", SensorType.Temperature, null);
        var r = Create(null, p).PollOnce().Find("t")!;

        Assert.Null(r.Value);
        Assert.Equal(DataQuality.Missing, r.Quality);
        Assert.Equal(SensorAvailability.NoData, r.Availability);
        Assert.Null(r.Minimum);
    }

    [Fact]
    public void Implausible_value_is_kept_but_flagged_suspect()
    {
        var p = new FakeProvider("lhm").WithDevice("cpu", HardwareCategory.Cpu)
            .Set("t", "cpu", "CPU Package", SensorType.Temperature, 255);
        var r = Create(null, p).PollOnce().Find("t")!;

        Assert.Equal(255, r.Value);
        Assert.Equal(DataQuality.Suspect, r.Quality);
    }

    [Fact]
    public void Higher_priority_provider_wins_duplicate_sensor_ids()
    {
        var a = new FakeProvider("A", priority: 0).Set("same", "d", "x", SensorType.Power, 10);
        var b = new FakeProvider("B", priority: 5).Set("same", "d", "x", SensorType.Power, 99);
        var snap = Create(null, b, a).PollOnce();

        Assert.Single(snap.Sensors);
        Assert.Equal(10, snap.Sensors[0].Value);
        Assert.Equal("A", snap.Sensors[0].ProviderName);
    }

    [Fact]
    public void Statistics_accumulate_over_polls_and_reset()
    {
        var p = new FakeProvider("lhm").Set("c", "d", "Clock", SensorType.Clock, 1000);
        var svc = Create(null, p);
        svc.PollOnce();
        p.Set("c", "d", "Clock", SensorType.Clock, 3000);
        var r = svc.PollOnce().Find("c")!;
        Assert.Equal(1000, r.Minimum);
        Assert.Equal(3000, r.Maximum);
        Assert.Equal(2000, r.Average);

        svc.ResetStatistics();
        r = svc.PollOnce().Find("c")!;
        Assert.Equal(3000, r.Minimum);
        Assert.Equal(1, r.SampleCount);
    }

    [Fact]
    public void Failing_provider_republishes_last_values_as_stale_then_drops_them()
    {
        var time = new ManualTimeProvider();
        var p = new FakeProvider("lhm").Set("t", "d", "Temp", SensorType.Temperature, 40);
        var svc = Create(time, p);
        svc.PollOnce();

        p.ThrowOnUpdate = true;
        time.Advance(TimeSpan.FromSeconds(2));
        var stale = svc.PollOnce().Find("t")!;
        Assert.Equal(DataQuality.Stale, stale.Quality);
        Assert.Equal(40, stale.Value);
        Assert.False(svc.ProviderStatuses["lhm"].IsAvailable);

        time.Advance(SensorPollingService.StaleAfter);
        Assert.Null(svc.PollOnce().Find("t"));
    }

    [Fact]
    public void Unavailable_provider_is_skipped()
    {
        var p = new FakeProvider("nv") { Status = Core.Abstractions.ProviderStatus.Unavailable("no gpu") }
            .Set("x", "d", "x", SensorType.Power, 1);
        var snap = Create(null, p).PollOnce();
        Assert.Empty(snap.Sensors);
        Assert.Equal(0, p.Updates);
    }

    [Fact]
    public void Throwing_subscriber_does_not_break_polling()
    {
        var p = new FakeProvider("lhm").Set("x", "d", "x", SensorType.Power, 1);
        var svc = Create(null, p);
        var received = 0;
        svc.SnapshotAvailable += (_, _) => throw new InvalidOperationException();
        svc.SnapshotAvailable += (_, _) => received++;
        svc.PollOnce();
        Assert.Equal(1, received);
    }

    [Fact]
    public void Interval_is_clamped_to_minimum()
    {
        var svc = Create();
        svc.Interval = TimeSpan.FromMilliseconds(10);
        Assert.Equal(SensorPollingService.MinimumInterval, svc.Interval);
    }

    [Fact]
    public async Task Background_loop_publishes_snapshots()
    {
        var p = new FakeProvider("lhm").Set("x", "d", "x", SensorType.Power, 1);
        await using var svc = Create(null, p);
        svc.Interval = SensorPollingService.MinimumInterval;
        var tcs = new TaskCompletionSource();
        var count = 0;
        svc.SnapshotAvailable += (_, _) => { if (Interlocked.Increment(ref count) >= 2) tcs.TrySetResult(); };
        svc.Start();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await svc.StopAsync();
        Assert.False(svc.IsRunning);
    }
}

public class SnapshotQueriesTests
{
    [Fact]
    public void Picks_preferred_sensor_by_name_within_category()
    {
        var devices = new[]
        {
            new HardwareDevice("cpu", "Ryzen", HardwareCategory.Cpu, "t"),
            new HardwareDevice("gpu", "RTX", HardwareCategory.Gpu, "t"),
        };
        var snap = Snapshots.Of(DateTimeOffset.Now, devices,
            Snapshots.Reading("1", "cpu", "Core #1", SensorType.Temperature, 60),
            Snapshots.Reading("2", "cpu", "Core (Tctl/Tdie)", SensorType.Temperature, 70),
            Snapshots.Reading("3", "gpu", "GPU Core", SensorType.Temperature, 50));

        Assert.Equal("2", snap.CpuTemperature()!.SensorId);
        Assert.Equal("3", snap.GpuTemperature()!.SensorId);
        Assert.Equal(70, snap.MaxProcessorTemperature());
        Assert.Null(snap.CpuPower());
    }
}
