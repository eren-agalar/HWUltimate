using Microsoft.Extensions.Logging;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Hardware;

namespace HWUltimate.Infrastructure.Discovery;

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

    /// <summary>
    /// Merges devices reported by several sources. Devices with a <see cref="DeviceInfo.PhysicalKey"/> merge only with the
    /// same key; a device without a key joins a keyed device of the same category and name only when exactly one such
    /// keyed device exists. Earlier sources win on conflicting values; every property is kept.
    /// </summary>
    internal static IReadOnlyList<DeviceInfo> Merge(IEnumerable<DeviceInfo> devices)
    {
        var input = devices.ToList();
        var result = new List<DeviceInfo>();
        var byKey = new Dictionary<(HardwareCategory, string), int>();
        var byName = new Dictionary<(HardwareCategory, string), List<int>>();

        void Index(int at, DeviceInfo d)
        {
            if (d.PhysicalKey is { } k) byKey[(d.Category, k)] = at;
            var nk = (d.Category, Normalize(d.Name));
            if (!byName.TryGetValue(nk, out var l)) byName[nk] = l = [];
            if (!l.Contains(at)) l.Add(at);
        }

        // Keyed devices first so keyless ones can attach to them regardless of source order.
        foreach (var d in input.Where(d => d.PhysicalKey is not null))
        {
            if (byKey.TryGetValue((d.Category, d.PhysicalKey!), out var at)) result[at] = Combine(result[at], d);
            else { result.Add(d); Index(result.Count - 1, d); }
        }

        foreach (var d in input.Where(d => d.PhysicalKey is null))
        {
            var nk = (d.Category, Normalize(d.Name));
            if (byName.TryGetValue(nk, out var candidates))
            {
                var keyed = candidates.Where(i => result[i].PhysicalKey is not null).ToList();
                var keyless = candidates.Where(i => result[i].PhysicalKey is null).ToList();
                if (keyed.Count == 1) { result[keyed[0]] = Combine(result[keyed[0]], d); continue; }
                if (keyed.Count == 0 && keyless.Count == 1) { result[keyless[0]] = Combine(result[keyless[0]], d); continue; }
                if (keyed.Count > 1) { result.Add(d); continue; } // ambiguous: several physical devices share this name
            }

            result.Add(d);
            Index(result.Count - 1, d);
        }

        // Stable order: category, then first appearance.
        return result
            .Select((d, i) => (d, i))
            .OrderBy(x => x.d.Category).ThenBy(x => x.i)
            .Select(x => x.d with { Properties = x.d.Properties.Select(PropertySectionMap.Assign).ToList() })
            .ToList();
    }

    private static DeviceInfo Combine(DeviceInfo existing, DeviceInfo d)
    {
        var names = existing.Properties.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return existing with
        {
            Manufacturer = existing.Manufacturer ?? d.Manufacturer,
            Model = existing.Model ?? d.Model,
            SerialNumber = existing.SerialNumber ?? d.SerialNumber,
            DriverVersion = existing.DriverVersion ?? d.DriverVersion,
            PhysicalKey = existing.PhysicalKey ?? d.PhysicalKey,
            Source = existing.Source.Contains(d.Source, StringComparison.Ordinal) ? existing.Source : $"{existing.Source}, {d.Source}",
            // A source's "Not Available" entry is replaced when another source does provide the value.
            Properties = existing.Properties
                .Select(p => !p.IsAvailable && d.Properties.FirstOrDefault(q => q.IsAvailable && q.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) is { } better ? better : p)
                .Concat(d.Properties.Where(p => !names.Contains(p.Name)))
                .ToList(),
        };
    }

    private static string Normalize(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
