using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Sensors;

/// <summary>
/// Polls every registered <see cref="IHardwareProvider"/> on a fixed cadence, normalises readings into
/// <see cref="SensorReading"/>s, attaches session statistics and publishes an immutable <see cref="SensorSnapshot"/>.
/// </summary>
public sealed class SensorPollingService : ISensorPollingService
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<IHardwareProvider> _providers;
    private readonly SensorStatisticsTracker _stats;
    private readonly ILogger<SensorPollingService> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _pollGate = new();
    private readonly Dictionary<string, (SensorReading Reading, DateTimeOffset LastGood)> _lastGood = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _consecutiveFailures = new(StringComparer.Ordinal);

    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile SensorSnapshot _current = SensorSnapshot.Empty;

    public SensorPollingService(
        IEnumerable<IHardwareProvider> providers,
        SensorStatisticsTracker stats,
        ILogger<SensorPollingService> logger,
        TimeProvider? timeProvider = null)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
        _stats = stats;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        foreach (var p in _providers)
        {
            _statuses[p.Name] = ProviderStatus.NotInitialized;
        }
    }

    public event EventHandler<SensorSnapshot>? SnapshotAvailable;

    public TimeSpan Interval
    {
        get => _interval;
        set => _interval = value < MinimumInterval ? MinimumInterval : value;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public SensorSnapshot Current => _current;

    public IReadOnlyDictionary<string, ProviderStatus> ProviderStatuses
    {
        get { lock (_pollGate) return new Dictionary<string, ProviderStatus>(_statuses); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                await provider.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Provider {Provider} initialised: {Status}", provider.Name, provider.Status.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Provider {Provider} failed to initialise", provider.Name);
            }

            lock (_pollGate)
            {
                _statuses[provider.Name] = provider.Status;
            }
        }
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => RunLoopAsync(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _logger.LogInformation("Sensor polling started at {Interval} ms", _interval.TotalMilliseconds);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var started = _time.GetTimestamp();
            try
            {
                PollOnce();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected polling failure");
            }

            var remaining = _interval - _time.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _time, token).ConfigureAwait(false);
            }
        }
    }

    public SensorSnapshot PollOnce()
    {
        lock (_pollGate)
        {
            var sw = Stopwatch.StartNew();
            var now = _time.GetLocalNow();
            var devices = new List<HardwareDevice>();
            var readings = new List<SensorReading>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var provider in _providers)
            {
                if (!provider.Status.IsAvailable)
                {
                    _statuses[provider.Name] = provider.Status;
                    continue;
                }

                IReadOnlyList<RawSensorReading> raw;
                try
                {
                    provider.Update();
                    raw = provider.ReadSensors();
                    devices.AddRange(provider.GetDevices());
                    _consecutiveFailures[provider.Name] = 0;
                    _statuses[provider.Name] = provider.Status;
                }
                catch (Exception ex)
                {
                    var failures = _consecutiveFailures.GetValueOrDefault(provider.Name) + 1;
                    _consecutiveFailures[provider.Name] = failures;
                    if (failures == 1 || failures % 60 == 0)
                    {
                        _logger.LogWarning(ex, "Provider {Provider} failed to update ({Failures} consecutive)", provider.Name, failures);
                    }

                    _statuses[provider.Name] = ProviderStatus.Unavailable($"Read error: {ex.Message}");
                    readings.AddRange(StaleReadingsFor(provider.Name, now, seen));
                    continue;
                }

                foreach (var r in raw)
                {
                    if (!seen.Add(r.SensorId))
                    {
                        continue; // a higher-priority provider already reported this id
                    }

                    readings.Add(Normalize(r, now));
                }
            }

            var snapshot = new SensorSnapshot(now, devices, readings, sw.Elapsed);
            _current = snapshot;
            RaiseSnapshot(snapshot);
            return snapshot;
        }
    }

    private SensorReading Normalize(RawSensorReading r, DateTimeOffset now)
    {
        var value = r.Value is { } v && double.IsFinite(v) ? v : (double?)null;
        var stats = _stats.Record(r.SensorId, value);

        var quality = value switch
        {
            null => DataQuality.Missing,
            { } x when !SensorPlausibility.IsPlausible(r.Type, r.Unit, x) => DataQuality.Suspect,
            _ => DataQuality.Good,
        };

        var availability = value is null && r.Availability == SensorAvailability.Available
            ? SensorAvailability.NoData
            : r.Availability;

        var reading = new SensorReading
        {
            SensorId = r.SensorId,
            DeviceId = r.DeviceId,
            Name = r.Name,
            Type = r.Type,
            Unit = r.Unit,
            Value = value,
            Minimum = stats.Minimum,
            Maximum = stats.Maximum,
            Average = stats.Average,
            SampleCount = stats.Count,
            Timestamp = now,
            ProviderName = r.ProviderName,
            Availability = availability,
            Quality = quality,
            Index = r.Index,
        };

        if (value is not null)
        {
            _lastGood[r.SensorId] = (reading, now);
        }

        return reading;
    }

    /// <summary>
    /// When a provider fails for a cycle, re-publish its last real values for a short window marked <see cref="DataQuality.Stale"/>.
    /// After <see cref="StaleAfter"/> the sensors disappear instead of showing old numbers indefinitely.
    /// </summary>
    private IEnumerable<SensorReading> StaleReadingsFor(string providerName, DateTimeOffset now, HashSet<string> seen)
    {
        foreach (var (id, entry) in _lastGood)
        {
            if (entry.Reading.ProviderName != providerName || now - entry.LastGood > StaleAfter || !seen.Add(id))
            {
                continue;
            }

            yield return entry.Reading with { Quality = DataQuality.Stale, Availability = SensorAvailability.Error };
        }
    }

    private void RaiseSnapshot(SensorSnapshot snapshot)
    {
        var handlers = SnapshotAvailable;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<SensorSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot subscriber {Subscriber} threw", handler.Method.DeclaringType?.Name);
            }
        }
    }

    public void ResetStatistics()
    {
        _stats.Reset();
        _logger.LogInformation("Sensor session statistics reset");
    }

    public void ResetStatistics(string sensorId) => _stats.Reset(sensorId);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var provider in _providers)
        {
            try
            {
                provider.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} threw on dispose", provider.Name);
            }
        }
    }
}
