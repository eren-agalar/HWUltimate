using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Core.Identity;

/// <summary>Fills in missing identity data (e.g. maps a PnP instance path to its PCI bus location).</summary>
public interface IDeviceIdentityResolver
{
    DeviceIdentity Resolve(DeviceIdentity identity);
}

/// <summary>
/// Supplies a descriptive name where a provider only reports a generic one (e.g. "Total Memory" becomes the installed
/// modules read from SMBIOS). Returns null to keep the provider's name. Never invents hardware details.
/// </summary>
public interface IDeviceNameResolver
{
    string? DisplayName(HardwareDevice device);
}

public sealed class NullIdentityResolver : IDeviceIdentityResolver
{
    public static NullIdentityResolver Instance { get; } = new();
    public DeviceIdentity Resolve(DeviceIdentity identity) => identity;
}

/// <summary>Result of merging provider devices: physical devices and the provider-device → physical-device map.</summary>
public sealed record DeviceMergeResult(IReadOnlyList<HardwareDevice> Devices, IReadOnlyDictionary<string, string> PhysicalIdOf);

/// <summary>
/// Builds one physical device per piece of hardware from the devices reported by every provider.
/// Devices are merged only on verifiable identity — PCI location, PnP instance path, or PCI vendor/device/subsystem ids
/// when they are unambiguous — never on name alone. Two devices from the same provider are never merged, so two
/// identical GPUs stay separate, as do an integrated and a discrete GPU.
/// </summary>
public static class PhysicalDeviceMerger
{
    public static DeviceMergeResult Merge(IReadOnlyList<HardwareDevice> devices, Func<string, int> providerPriority, IDeviceIdentityResolver? resolver = null)
    {
        resolver ??= NullIdentityResolver.Instance;
        var list = devices.DistinctBy(d => d.DeviceId).Select(d => d.Identity is { } id ? d with { Identity = resolver.Resolve(id) } : d).ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();
        var members = Enumerable.Range(0, list.Count).Select(i => new HashSet<int> { i }).ToArray();

        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        bool TryUnion(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return true;
            if (list[a].Category != list[b].Category) return false;
            // A physical device is seen at most once per provider: refuse unions that would put two devices of one provider together.
            var providersA = members[ra].Select(i => list[i].ProviderName).ToHashSet(StringComparer.Ordinal);
            if (members[rb].Any(i => providersA.Contains(list[i].ProviderName))) return false;
            // Never join groups whose known PCI locations disagree.
            var locA = members[ra].Select(i => list[i].Identity?.Location).OfType<PciLocation>().Distinct().ToList();
            var locB = members[rb].Select(i => list[i].Identity?.Location).OfType<PciLocation>().Distinct().ToList();
            if (locA.Count > 0 && locB.Count > 0 && !locA.Intersect(locB).Any()) return false;
            parent[rb] = ra;
            members[ra].UnionWith(members[rb]);
            return true;
        }

        void UnionBy<TKey>(Func<DeviceIdentity, TKey?> key) where TKey : notnull
        {
            foreach (var g in Enumerable.Range(0, list.Count)
                         .Where(i => list[i].Identity is { } id && key(id) is not null)
                         .GroupBy(i => (list[i].Category, key(list[i].Identity!)!)))
            {
                var ids = g.ToList();
                for (var k = 1; k < ids.Count; k++) TryUnion(ids[0], ids[k]);
            }
        }

        // 1. Exact hardware location / instance path / logical singleton.
        UnionBy(id => id.Location is { } l ? l.ToString() : null);
        UnionBy(id => id.InstancePath);
        UnionBy(id => id.SingletonKey);

        // 2. PCI ids, only when unambiguous: every provider reports at most one device with that id.
        foreach (var g in Enumerable.Range(0, list.Count)
                     .Where(i => list[i].Identity is { VendorId: not null, DeviceId: not null })
                     .GroupBy(i => (list[i].Category, list[i].Identity!.VendorId, list[i].Identity!.DeviceId)))
        {
            var ids = g.ToList();
            if (ids.Count < 2) continue;
            var ambiguous = ids.GroupBy(i => list[i].ProviderName).Any(p => p.Count() > 1);
            if (ambiguous) continue;
            // Subsystem ids, when both sides know them, must also agree.
            for (var k = 1; k < ids.Count; k++)
            {
                var a = list[ids[0]].Identity!;
                var b = list[ids[k]].Identity!;
                if (a.SubsystemId is { } sa && b.SubsystemId is { } sb && (sa != sb || a.SubsystemVendorId != b.SubsystemVendorId)) continue;
                TryUnion(ids[0], ids[k]);
            }
        }

        // 3. Materialise physical devices.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<HardwareDevice>();
        foreach (var root in Enumerable.Range(0, list.Count).Where(i => Find(i) == i))
        {
            var group = members[root].Select(i => list[i]).OrderBy(d => providerPriority(d.ProviderName)).ToList();
            var primary = group[0];
            var identity = MergeIdentity(group.Select(d => d.Identity).OfType<DeviceIdentity>());
            var physicalId = group.Count == 1 ? primary.DeviceId : PhysicalId(identity, primary);
            foreach (var d in group) map[d.DeviceId] = physicalId;
            result.Add(primary with
            {
                DeviceId = physicalId,
                Identity = identity,
                Vendor = group.Select(d => d.Vendor).FirstOrDefault(v => v is not null),
                Providers = group.Select(d => d.ProviderName).Distinct().ToList(),
                SourceDeviceIds = group.Select(d => d.DeviceId).ToList(),
                Relevance = group.Min(d => d.Relevance),
            });
        }

        // Re-point parents (e.g. Super I/O under motherboard) at physical ids; keep original order.
        var order = list.Select((d, i) => (d.DeviceId, i)).ToDictionary(x => x.DeviceId, x => x.i);
        result = result
            .Select(d => d.ParentDeviceId is { } p && map.TryGetValue(p, out var pp) ? d with { ParentDeviceId = pp } : d)
            .OrderBy(d => d.SourceDeviceIds.Min(id => order[id]))
            .ToList();
        return new DeviceMergeResult(result, map);
    }

    internal static string PhysicalId(DeviceIdentity? id, HardwareDevice primary) => id switch
    {
        { Location: { } l } => $"pci:{l}",
        { InstancePath: { } p } => $"pnp:{p}",
        { SingletonKey: { } k } => $"sys:{k}",
        { VendorId: { } v, DeviceId: { } d } => $"pciid:{v:X4}:{d:X4}:{primary.Category}",
        _ => primary.DeviceId,
    };

    internal static DeviceIdentity? MergeIdentity(IEnumerable<DeviceIdentity> ids)
    {
        DeviceIdentity? merged = null;
        foreach (var id in ids)
        {
            merged = merged is null ? id : merged with
            {
                InstancePath = merged.InstancePath ?? id.InstancePath,
                Location = merged.Location ?? id.Location,
                VendorId = merged.VendorId ?? id.VendorId,
                DeviceId = merged.DeviceId ?? id.DeviceId,
                SubsystemVendorId = merged.SubsystemVendorId ?? id.SubsystemVendorId,
                SubsystemId = merged.SubsystemId ?? id.SubsystemId,
                SingletonKey = merged.SingletonKey ?? id.SingletonKey,
            };
        }

        return merged;
    }

    /// <summary>
    /// Within one physical device, the same measurement (type + unit + name) reported by several providers is kept once,
    /// from the highest-priority provider; the others are recorded in <c>AlsoReportedBy</c>. Returns the ids to drop.
    /// </summary>
    public static (HashSet<string> Suppressed, Dictionary<string, List<string>> AlsoReportedBy) FindDuplicateSensors(
        IReadOnlyList<RawSensorReading> readings, IReadOnlyDictionary<string, string> physicalIdOf, Func<string, int> providerPriority)
    {
        var (suppressed, also, _) = FindDuplicates(readings, physicalIdOf, providerPriority);
        return (suppressed, also);
    }

    /// <summary>As <see cref="FindDuplicateSensors"/>, also returning the suppressed ids per kept sensor.</summary>
    public static (HashSet<string> Suppressed, Dictionary<string, List<string>> AlsoReportedBy, Dictionary<string, List<string>> DuplicateIds) FindDuplicates(
        IReadOnlyList<RawSensorReading> readings, IReadOnlyDictionary<string, string> physicalIdOf, Func<string, int> providerPriority)
    {
        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        var also = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dupIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var g in readings.GroupBy(r => (Device: physicalIdOf.GetValueOrDefault(r.DeviceId, r.DeviceId), r.Type, r.Unit, Name: NormalizeName(r.Name))))
        {
            if (g.Select(r => r.ProviderName).Distinct().Count() < 2) continue;
            var ordered = g.OrderBy(r => providerPriority(r.ProviderName)).ToList();
            var keepProvider = ordered[0].ProviderName;
            var keep = ordered.Where(r => r.ProviderName == keepProvider).ToList();
            foreach (var r in ordered.Where(r => r.ProviderName != keepProvider))
            {
                suppressed.Add(r.SensorId);
                foreach (var k in keep)
                {
                    if (!also.TryGetValue(k.SensorId, out var l)) also[k.SensorId] = l = [];
                    if (!l.Contains(r.ProviderName)) l.Add(r.ProviderName);
                    if (!dupIds.TryGetValue(k.SensorId, out var ids)) dupIds[k.SensorId] = ids = [];
                    ids.Add(r.SensorId);
                }
            }
        }

        return (suppressed, also, dupIds);
    }

    internal static string NormalizeName(string name) =>
        string.Join(' ', name.ToLowerInvariant().Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
}
