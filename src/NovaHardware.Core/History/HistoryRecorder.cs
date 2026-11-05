using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.History;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.History;

/// <summary>
/// Subscribes to the polling service and writes real sensor values to <see cref="IHistoricalDataService"/>
/// in batches on a background task so the polling loop is never blocked by disk I/O.
/// </summary>
public sealed class HistoryRecorder : IAsyncDisposable
{
    private readonly ISensorPollingService _polling;
    private readonly IHistoricalDataService _history;
    private readonly ILogger<HistoryRecorder> _logger;
    private readonly Channel<SensorSnapshot> _channel = Channel.CreateBounded<SensorSnapshot>(
        new BoundedChannelOptions(120) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly HashSet<string> _knownSensors = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _writer;
    private DateTimeOffset _lastRecorded = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public HistoryRecorder(ISensorPollingService polling, IHistoricalDataService history, ILogger<HistoryRecorder> logger)
    {
        _polling = polling;
        _history = history;
        _logger = logger;
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Minimum spacing between persisted samples. Polls in between are not stored.</summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(14);

    public long SamplesWritten { get; private set; }

    public void Start()
    {
        if (_writer is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _polling.SnapshotAvailable += OnSnapshot;
        _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    private void OnSnapshot(object? sender, SensorSnapshot snapshot)
    {
        if (!Enabled || snapshot.Timestamp - _lastRecorded < SampleInterval)
        {
            return;
        }

        _lastRecorded = snapshot.Timestamp;
        _channel.Writer.TryWrite(snapshot);
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        await foreach (var snapshot in _channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try
            {
                await PersistAsync(snapshot, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist sensor history");
            }
        }
    }

    internal async Task PersistAsync(SensorSnapshot snapshot, CancellationToken token)
    {
        var deviceNames = snapshot.Devices.ToDictionary(d => d.DeviceId, d => d.Name, StringComparer.Ordinal);
        var newDescriptors = snapshot.Sensors
            .Where(s => !_knownSensors.Contains(s.SensorId))
            .Select(s => new SensorDescriptor(s.SensorId, s.DeviceId, deviceNames.GetValueOrDefault(s.DeviceId, s.DeviceId),
                s.Name, s.Type, s.Unit, s.ProviderName))
            .ToList();

        if (newDescriptors.Count > 0)
        {
            await _history.UpsertDescriptorsAsync(newDescriptors, token).ConfigureAwait(false);
            foreach (var d in newDescriptors)
            {
                _knownSensors.Add(d.SensorId);
            }
        }

        // Only fresh, real values are stored. Stale re-publications and missing values are skipped.
        var samples = snapshot.Sensors
            .Where(s => s.Value.HasValue && s.Quality is DataQuality.Good or DataQuality.Suspect)
            .Select(s => new SensorSample(s.SensorId, snapshot.Timestamp, s.Value!.Value))
            .ToList();

        if (samples.Count > 0)
        {
            await _history.AppendAsync(samples, token).ConfigureAwait(false);
            SamplesWritten += samples.Count;
        }

        if (snapshot.Timestamp - _lastPurge > TimeSpan.FromHours(1))
        {
            _lastPurge = snapshot.Timestamp;
            var removed = await _history.PurgeOlderThanAsync(snapshot.Timestamp - Retention, token).ConfigureAwait(false);
            if (removed > 0)
            {
                _logger.LogInformation("Purged {Count} history samples older than {Days} days", removed, Retention.TotalDays);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _polling.SnapshotAvailable -= OnSnapshot;
        _channel.Writer.TryComplete();
        if (_cts is not null)
        {
            if (_writer is not null)
            {
                // Give the writer a moment to flush the final batch.
                await Task.WhenAny(_writer, Task.Delay(2000)).ConfigureAwait(false);
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
