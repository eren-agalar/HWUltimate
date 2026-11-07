namespace HWUltimate.Core.Sensors;

/// <summary>
/// Thread-safe running min / max / mean per sensor. Only real (non-null, finite) values are accumulated.
/// </summary>
public sealed class SensorStatisticsTracker
{
    private readonly Dictionary<string, Accumulator> _stats = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public readonly record struct Stats(double? Minimum, double? Maximum, double? Average, long Count);

    public Stats Record(string sensorId, double? value)
    {
        lock (_gate)
        {
            if (!_stats.TryGetValue(sensorId, out var acc))
            {
                acc = new Accumulator();
                _stats[sensorId] = acc;
            }

            if (value is { } v && double.IsFinite(v))
            {
                acc.Add(v);
            }

            return acc.ToStats();
        }
    }

    public Stats Get(string sensorId)
    {
        lock (_gate)
        {
            return _stats.TryGetValue(sensorId, out var acc) ? acc.ToStats() : default;
        }
    }

    /// <summary>Resets statistics for a single sensor; the next real sample starts a new min/max/average.</summary>
    public void Reset(string sensorId)
    {
        lock (_gate)
        {
            _stats.Remove(sensorId);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _stats.Clear();
        }
    }

    private sealed class Accumulator
    {
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private double _mean;
        private long _count;

        public void Add(double value)
        {
            _count++;
            if (value < _min) _min = value;
            if (value > _max) _max = value;
            // Welford-style incremental mean: numerically stable over very long sessions.
            _mean += (value - _mean) / _count;
        }

        public Stats ToStats() => _count == 0
            ? new Stats(null, null, null, 0)
            : new Stats(_min, _max, _mean, _count);
    }
}
