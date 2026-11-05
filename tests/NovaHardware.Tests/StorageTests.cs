using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.Core.History;
using NovaHardware.Core.Sensors;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.History;
using NovaHardware.Domain.Sensors;
using NovaHardware.Storage;

namespace NovaHardware.Tests;

public sealed class StorageTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nova-test-{Guid.NewGuid():N}.db");
    private SqliteHistoricalDataService _db = null!;
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        _db = new SqliteHistoricalDataService(_path, NullLogger<SqliteHistoricalDataService>.Instance);
        await _db.InitializeAsync();
        await _db.UpsertDescriptorsAsync([new SensorDescriptor("s1", "cpu", "CPU", "Package", SensorType.Temperature, SensorUnit.Celsius, "lhm")]);
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }

        return ValueTask.CompletedTask;
    }

    private Task Seed(int count) =>
        _db.AppendAsync(Enumerable.Range(0, count).Select(i => new SensorSample("s1", T0.AddSeconds(i), i)).ToList());

    [Fact]
    public async Task Round_trips_samples_in_order()
    {
        await Seed(10);
        var samples = await _db.GetSamplesAsync(new HistoryQuery("s1", T0, T0.AddSeconds(4)));
        Assert.Equal([0d, 1, 2, 3, 4], samples.Select(s => s.Value));
        Assert.Equal(T0.AddSeconds(2), samples[2].Timestamp);
    }

    [Fact]
    public async Task Descriptors_persist_with_units()
    {
        var d = Assert.Single(await _db.GetSensorsAsync());
        Assert.Equal(SensorUnit.Celsius, d.Unit);
        Assert.Equal("CPU", d.DeviceName);
    }

    [Fact]
    public async Task Aggregates_into_buckets()
    {
        await Seed(60);
        var buckets = await _db.GetAggregatedAsync(new HistoryQuery("s1", T0, T0.AddMinutes(1), TimeSpan.FromSeconds(10)));
        Assert.Equal(6, buckets.Count);
        Assert.Equal(0, buckets[0].Minimum);
        Assert.Equal(9, buckets[0].Maximum);
        Assert.Equal(4.5, buckets[0].Average);
        Assert.Equal(10, buckets[0].Count);
    }

    [Fact]
    public async Task Statistics_over_range()
    {
        await Seed(11);
        var st = await _db.GetStatisticsAsync("s1", T0, T0.AddSeconds(10));
        Assert.NotNull(st);
        Assert.Equal(0, st.Minimum);
        Assert.Equal(10, st.Maximum);
        Assert.Equal(5, st.Average);
        Assert.Equal(11, st.Count);
        Assert.Null(await _db.GetStatisticsAsync("s1", T0.AddDays(1), T0.AddDays(2)));
    }

    [Fact]
    public async Task Purge_removes_old_samples()
    {
        await Seed(10);
        var removed = await _db.PurgeOlderThanAsync(T0.AddSeconds(5));
        Assert.Equal(5, removed);
        Assert.Equal(5, await _db.GetSampleCountAsync());
    }

    [Fact]
    public async Task Unknown_sensor_returns_empty()
    {
        Assert.Empty(await _db.GetSamplesAsync(new HistoryQuery("nope", T0, T0.AddDays(1))));
        Assert.Empty(await _db.GetAggregatedAsync(new HistoryQuery("nope", T0, T0.AddDays(1))));
    }

    [Fact]
    public async Task Non_finite_values_are_never_stored()
    {
        await _db.AppendAsync([new SensorSample("s1", T0, double.NaN), new SensorSample("s1", T0.AddSeconds(1), 42)]);
        Assert.Equal(1, await _db.GetSampleCountAsync());
    }

    [Fact]
    public async Task Survives_reopen()
    {
        await Seed(3);
        using var reopened = new SqliteHistoricalDataService(_path, NullLogger<SqliteHistoricalDataService>.Instance);
        await reopened.InitializeAsync();
        Assert.Equal(3, (await reopened.GetSamplesAsync(new HistoryQuery("s1", T0, T0.AddMinutes(1)))).Count);
    }

    [Fact]
    public void Auto_bucket_keeps_point_count_bounded()
    {
        foreach (var range in new[] { TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromHours(24), TimeSpan.FromDays(7) })
        {
            Assert.True(range / SqliteHistoricalDataService.AutoBucket(range) <= 600);
        }
    }

    [Fact]
    public async Task Recorder_persists_only_real_values()
    {
        var polling = new SensorPollingService([], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
        await using var recorder = new HistoryRecorder(polling, _db, NullLogger<HistoryRecorder>.Instance);
        var devices = new[] { new HardwareDevice("gpu", "GPU", HardwareCategory.Gpu, "t") };
        var snap = Snapshots.Of(T0, devices,
            Snapshots.Reading("g1", "gpu", "Core", SensorType.Temperature, 50),
            Snapshots.Reading("g2", "gpu", "Hot Spot", SensorType.Temperature, null, DataQuality.Missing),
            Snapshots.Reading("g3", "gpu", "Memory", SensorType.Temperature, 60, DataQuality.Stale));

        await recorder.PersistAsync(snap, CancellationToken.None);

        Assert.Equal(1, recorder.SamplesWritten);
        Assert.Contains(await _db.GetSensorsAsync(), d => d.SensorId == "g2"); // descriptor registered even without data
        Assert.Single(await _db.GetSamplesAsync(new HistoryQuery("g1", T0.AddSeconds(-1), T0.AddSeconds(1))));
    }
}
