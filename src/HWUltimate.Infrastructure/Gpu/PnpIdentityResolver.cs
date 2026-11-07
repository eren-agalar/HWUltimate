using HWUltimate.Core.Identity;
using HWUltimate.Domain.Hardware;

namespace HWUltimate.Infrastructure.Gpu;

/// <summary>
/// Completes device identities from the Windows PnP tree: PnP instance path ↔ PCI bus/device/function.
/// The PCI device table is read once and refreshed at most once a minute when a lookup misses.
/// </summary>
public sealed class PnpIdentityResolver : IDeviceIdentityResolver
{
    private static readonly TimeSpan RefreshAfterMiss = TimeSpan.FromMinutes(1);
    private readonly Func<IReadOnlyDictionary<string, PciDeviceProperties>> _source;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, PciDeviceProperties>? _byPath;
    private Dictionary<PciLocation, string> _byLocation = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public PnpIdentityResolver(Func<IReadOnlyDictionary<string, PciDeviceProperties>>? source = null, TimeProvider? time = null)
    {
        _source = source ?? DefaultSource;
        _time = time ?? TimeProvider.System;
    }

    private static IReadOnlyDictionary<string, PciDeviceProperties> DefaultSource() =>
        OperatingSystem.IsWindows() ? PnpDeviceProperties.ReadPciDevices() : new Dictionary<string, PciDeviceProperties>();

    public DeviceIdentity Resolve(DeviceIdentity identity)
    {
        lock (_gate)
        {
            EnsureLoaded(false);
            var result = Apply(identity);
            var incomplete = (identity.InstancePath is not null && result.Location is null) || (identity.Location is not null && result.InstancePath is null);
            if (incomplete && _time.GetUtcNow() - _loadedAt > RefreshAfterMiss)
            {
                EnsureLoaded(true);
                result = Apply(identity);
            }

            return result;
        }
    }

    private DeviceIdentity Apply(DeviceIdentity id)
    {
        if (_byPath is null) return id;
        if (id.InstancePath is { } path && id.Location is null && _byPath.TryGetValue(path, out var p) && Location(p) is { } loc)
        {
            id = id with { Location = loc };
        }

        if (id.Location is { } l && id.InstancePath is null && _byLocation.TryGetValue(l, out var instance))
        {
            var fromPath = PnpPath.FromPath(instance);
            id = id with
            {
                InstancePath = fromPath.InstancePath,
                VendorId = id.VendorId ?? fromPath.VendorId,
                DeviceId = id.DeviceId ?? fromPath.DeviceId,
                SubsystemVendorId = id.SubsystemVendorId ?? fromPath.SubsystemVendorId,
                SubsystemId = id.SubsystemId ?? fromPath.SubsystemId,
            };
        }

        return id;
    }

    /// <summary>Windows reports DEVPKEY_Device_Address for PCI as (device &lt;&lt; 16) | function. The PCI segment is assumed to be 0.</summary>
    internal static PciLocation? Location(PciDeviceProperties p) =>
        p.BusNumber is { } bus && p.Device is { } dev && p.Function is { } fn ? new PciLocation(0, (int)bus, (int)dev, (int)fn) : null;

    private void EnsureLoaded(bool force)
    {
        if (_byPath is not null && !force) return;
        try
        {
            var source = _source();
            _byPath = source.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => kv.Value, StringComparer.Ordinal);
            _byLocation = [];
            foreach (var (path, props) in _byPath)
            {
                if (Location(props) is { } loc) _byLocation.TryAdd(loc, path);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            _byPath ??= new Dictionary<string, PciDeviceProperties>();
        }

        _loadedAt = _time.GetUtcNow();
    }
}
