using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.History;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Storage;

/// <summary>
/// Lightweight SQLite repository for sensor history. Samples are stored as (sensor_key, unix-ms, value) in a
/// WITHOUT ROWID table clustered on (sensor_key, ts) so range scans for one sensor are sequential reads.
/// </summary>
public sealed class SqliteHistoricalDataService : IHistoricalDataService, IDisposable
{
    private const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly ILogger<SqliteHistoricalDataService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<string, long> _sensorKeys = new(StringComparer.Ordinal);
    private SqliteConnection? _keepAlive;

    public SqliteHistoricalDataService(string databasePath, ILogger<SqliteHistoricalDataService> logger)
    {
        DatabasePath = databasePath;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = databasePath == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = databasePath == ":memory:" ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (DatabasePath != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        else
        {
            // Shared in-memory databases live only while a connection is open.
            _keepAlive = await OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecAsync(c, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sensors (
                sensor_key  INTEGER PRIMARY KEY AUTOINCREMENT,
                sensor_id   TEXT NOT NULL UNIQUE,
                device_id   TEXT NOT NULL,
                device_name TEXT NOT NULL,
                name        TEXT NOT NULL,
                type        INTEGER NOT NULL,
                unit        INTEGER NOT NULL,
                provider    TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS samples (
                sensor_key INTEGER NOT NULL,
                ts         INTEGER NOT NULL,
                value      REAL NOT NULL,
                PRIMARY KEY (sensor_key, ts)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_samples_ts ON samples(ts);
            """, cancellationToken).ConfigureAwait(false);

        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES ('schema_version', $v)";
            cmd.Parameters.AddWithValue("$v", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT sensor_id, sensor_key FROM sensors";
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            lock (_sensorKeys)
            {
                while (r.Read())
                {
                    _sensorKeys[r.GetString(0)] = r.GetInt64(1);
                }
            }
        }

        _logger.LogInformation("History database ready at {Path} ({Sensors} known sensors)", DatabasePath, _sensorKeys.Count);
    }

    public async Task UpsertDescriptorsAsync(IEnumerable<SensorDescriptor> descriptors, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sensors(sensor_id, device_id, device_name, name, type, unit, provider)
                VALUES ($id, $dev, $devName, $name, $type, $unit, $provider)
                ON CONFLICT(sensor_id) DO UPDATE SET device_name = excluded.device_name, name = excluded.name, provider = excluded.provider
                RETURNING sensor_key;
                """;
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pDev = cmd.Parameters.Add("$dev", SqliteType.Text);
            var pDevName = cmd.Parameters.Add("$devName", SqliteType.Text);
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pType = cmd.Parameters.Add("$type", SqliteType.Integer);
            var pUnit = cmd.Parameters.Add("$unit", SqliteType.Integer);
            var pProv = cmd.Parameters.Add("$provider", SqliteType.Text);

            foreach (var d in descriptors)
            {
                pId.Value = d.SensorId;
                pDev.Value = d.DeviceId;
                pDevName.Value = d.DeviceName;
                pName.Value = d.Name;
                pType.Value = (int)d.Type;
                pUnit.Value = (int)d.Unit;
                pProv.Value = d.ProviderName;
                var key = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                lock (_sensorKeys)
                {
                    _sensorKeys[d.SensorId] = key;
                }
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task AppendAsync(IReadOnlyCollection<SensorSample> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO samples(sensor_key, ts, value) VALUES ($k, $ts, $v)";
            var pK = cmd.Parameters.Add("$k", SqliteType.Integer);
            var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var pV = cmd.Parameters.Add("$v", SqliteType.Real);
            await cmd.PrepareAsync(cancellationToken).ConfigureAwait(false);

            var skipped = 0;
            foreach (var s in samples)
            {
                if (!double.IsFinite(s.Value) || !TryGetKey(s.SensorId, out var key))
                {
                    skipped++;
                    continue;
                }

                pK.Value = key;
                pTs.Value = s.Timestamp.ToUnixTimeMilliseconds();
                pV.Value = s.Value;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (skipped > 0)
            {
                _logger.LogDebug("Skipped {Count} samples for unregistered sensors", skipped);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private bool TryGetKey(string sensorId, out long key)
    {
        lock (_sensorKeys)
        {
            return _sensorKeys.TryGetValue(sensorId, out key);
        }
    }

    public async Task<IReadOnlyList<SensorDescriptor>> GetSensorsAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sensor_id, device_id, device_name, name, type, unit, provider FROM sensors ORDER BY device_name, type, name";
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<SensorDescriptor>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new SensorDescriptor(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                (SensorType)r.GetInt32(4), (SensorUnit)r.GetInt32(5), r.GetString(6)));
        }

        return list;
    }

    public async Task<IReadOnlyList<SensorSample>> GetSamplesAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        if (!TryGetKey(query.SensorId, out var key))
        {
            return [];
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, value FROM samples WHERE sensor_key = $k AND ts BETWEEN $from AND $to ORDER BY ts";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$from", query.From.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", query.To.ToUnixTimeMilliseconds());
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<SensorSample>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new SensorSample(query.SensorId, DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).ToLocalTime(), r.GetDouble(1)));
        }

        return list;
    }

    public async Task<IReadOnlyList<AggregatedSample>> GetAggregatedAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        if (!TryGetKey(query.SensorId, out var key))
        {
            return [];
        }

        var bucket = query.BucketSize ?? AutoBucket(query.To - query.From);
        var bucketMs = Math.Max(1L, (long)bucket.TotalMilliseconds);

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT (ts / $b) * $b AS bucket, MIN(value), MAX(value), AVG(value), COUNT(*)
            FROM samples
            WHERE sensor_key = $k AND ts BETWEEN $from AND $to
            GROUP BY bucket ORDER BY bucket
            """;
        cmd.Parameters.AddWithValue("$b", bucketMs);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$from", query.From.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", query.To.ToUnixTimeMilliseconds());
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<AggregatedSample>();
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new AggregatedSample(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).ToLocalTime(),
                r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetInt32(4)));
        }

        return list;
    }

    /// <summary>Picks a bucket size giving roughly 300–600 points for the requested range.</summary>
    public static TimeSpan AutoBucket(TimeSpan range)
    {
        TimeSpan[] steps =
        [
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3),
        ];
        foreach (var s in steps)
        {
            if (range / s <= 600)
            {
                return s;
            }
        }

        return TimeSpan.FromHours(6);
    }

    public async Task<HistoryStatistics?> GetStatisticsAsync(string sensorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (!TryGetKey(sensorId, out var key))
        {
            return null;
        }

        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MIN(value), MAX(value), AVG(value), COUNT(*), MIN(ts), MAX(ts) FROM samples WHERE sensor_key = $k AND ts BETWEEN $from AND $to";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false) || r.GetInt64(3) == 0)
        {
            return null;
        }

        return new HistoryStatistics(sensorId, r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetInt64(3),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)).ToLocalTime(),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)).ToLocalTime());
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> GetSampleCountAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM samples";
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task ExecAsync(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        _writeLock.Dispose();
    }
}
