using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.History;
using NovaHardware.Domain.Reports;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Abstractions;

/// <summary>Discovers devices from OS-level sources (WMI, SMBIOS, SetupAPI).</summary>
public interface IDeviceDiscoveryService
{
    string SourceName { get; }
    Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds the full hardware inventory by combining all discovery sources.</summary>
public interface IHardwareInventoryService
{
    HardwareInventory Current { get; }
    Task<HardwareInventory> RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Supplies the OS-level system summary (OS, board, BIOS, CPU, RAM).</summary>
public interface ISystemSummaryProvider
{
    Task<SystemSummary> GetSummaryAsync(CancellationToken cancellationToken = default);
}

/// <summary>The single, central sensor polling loop. Every consumer (explorer, overlay, alerts) reads from it.</summary>
public interface ISensorPollingService : IAsyncDisposable
{
    TimeSpan Interval { get; set; }
    bool IsRunning { get; }
    SensorSnapshot Current { get; }
    IReadOnlyDictionary<string, ProviderStatus> ProviderStatuses { get; }

    event EventHandler<SensorSnapshot>? SnapshotAvailable;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    void Start();
    Task StopAsync();

    /// <summary>Runs a single poll synchronously (used at start-up and by tests).</summary>
    SensorSnapshot PollOnce();

    /// <summary>Resets session min / max / average for all sensors.</summary>
    void ResetStatistics();

    /// <summary>Resets session min / max / average for one sensor.</summary>
    void ResetStatistics(string sensorId);
}

/// <summary>Sensor history repository (retained for existing databases; not used by the v0.2 application).</summary>
public interface IHistoricalDataService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertDescriptorsAsync(IEnumerable<SensorDescriptor> descriptors, CancellationToken cancellationToken = default);
    Task AppendAsync(IReadOnlyCollection<SensorSample> samples, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SensorDescriptor>> GetSensorsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SensorSample>> GetSamplesAsync(HistoryQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AggregatedSample>> GetAggregatedAsync(HistoryQuery query, CancellationToken cancellationToken = default);
    Task<HistoryStatistics?> GetStatisticsAsync(string sensorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<long> GetSampleCountAsync(CancellationToken cancellationToken = default);
}

public interface IAlertService
{
    IReadOnlyList<AlertRule> Rules { get; }
    IReadOnlyList<ComponentAlertRule> ComponentRules { get; }
    IReadOnlyList<AlertEvent> RecentEvents { get; }
    int ActiveAlertCount { get; }

    event EventHandler<AlertEvent>? AlertRaised;

    void SetRules(IEnumerable<AlertRule> rules);
    void SetComponentRules(IEnumerable<ComponentAlertRule> rules);
    IReadOnlyList<AlertEvent> Evaluate(SensorSnapshot snapshot);
    void ClearHistory();
}

public interface IReportExportService
{
    IReadOnlyList<ReportFormat> SupportedFormats { get; }
    string Render(ReportData data, ReportFormat format);
    Task ExportAsync(ReportData data, ReportFormat format, string path, CancellationToken cancellationToken = default);
}

/// <summary>Loads and saves a small local preference document (JSON on disk in the default implementation).</summary>
public interface IPreferenceStore<T> where T : class, new()
{
    T Load();
    void Save(T value);
}
