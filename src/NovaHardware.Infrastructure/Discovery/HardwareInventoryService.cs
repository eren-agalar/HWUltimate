using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Hardware;

namespace NovaHardware.Infrastructure.Discovery;

/// <summary>
/// Combines all <see cref="IDeviceDiscoveryService"/> sources into one inventory. Devices reported by more than one
/// source (same category and name) are merged, keeping every property but never overwriting an earlier value.
/// </summary>
public sealed class HardwareInventoryService : IHardwareInventoryService
{
    private readonly IReadOnlyList<IDeviceDiscoveryService> _sources;
    private readonly ISystemSummaryProvider _summary;
    private readonly ILogger<HardwareInventoryService> _logger;

    public HardwareInventoryService(IEnumerable<IDeviceDiscoveryService> sources, ISystemSummaryProvider summary, ILogger<HardwareInventoryService> logger)
    {
        _sources = sources.ToList();
        _summary = summary;
        _logger = logger;
    }

    public HardwareInventory Current { get; private set; } = HardwareInventory.Empty;

    public async Task<HardwareInventory> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var summaryTask = SafeSummary(warnings, cancellationToken);
        var tasks = _sources.Select(async s =>
        {
            try
            {
                return await s.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Discovery source {Source} failed", s.SourceName);
                lock (warnings)
                {
                    warnings.Add($"{s.SourceName}: {ex.Message}");
                }

                return (IReadOnlyList<DeviceInfo>)[];
            }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var merged = Merge(results.SelectMany(r => r));
        Current = new HardwareInventory(DateTimeOffset.Now, await summaryTask.ConfigureAwait(false), merged, warnings);
        _logger.LogInformation("Hardware inventory collected: {Count} devices", merged.Count);
        return Current;
    }

    private async Task<SystemSummary> SafeSummary(List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await _summary.GetSummaryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "System summary failed");
            lock (warnings)
            {
                warnings.Add($"Summary: {ex.Message}");
            }

            return new SystemSummary();
        }
    }

    internal static IReadOnlyList<DeviceInfo> Merge(IEnumerable<DeviceInfo> devices)
    {
        var result = new List<DeviceInfo>();
        var index = new Dictionary<(HardwareCategory, string), int>();
        foreach (var d in devices)
        {
            var key = (d.Category, Normalize(d.Name));
            if (!index.TryGetValue(key, out var at))
            {
                index[key] = result.Count;
                result.Add(d);
                continue;
            }

            var existing = result[at];
            var names = existing.Properties.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result[at] = existing with
            {
                Manufacturer = existing.Manufacturer ?? d.Manufacturer,
                Model = existing.Model ?? d.Model,
                SerialNumber = existing.SerialNumber ?? d.SerialNumber,
                DriverVersion = existing.DriverVersion ?? d.DriverVersion,
                Source = existing.Source.Contains(d.Source, StringComparison.Ordinal) ? existing.Source : $"{existing.Source}, {d.Source}",
                Properties = existing.Properties.Concat(d.Properties.Where(p => !names.Contains(p.Name))).ToList(),
            };
        }

        return result.OrderBy(d => d.Category).ToList();
    }

    private static string Normalize(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
