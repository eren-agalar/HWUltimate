using System.Globalization;
using System.Text.RegularExpressions;
using HWUltimate.Core.Identity;
using HWUltimate.Domain.Hardware;

namespace HWUltimate.Infrastructure.Smbios;

/// <summary>
/// Gives devices a descriptive name where the sensor provider reports a generic one, using only data the system reports:
/// system memory becomes e.g. "32 GB DDR5-6000 (2 × 16 GB) · Kingston KF560C36-16" from SMBIOS type 17.
/// Devices with an empty name get their category name, so a sensor-view header is never blank.
/// </summary>
public sealed partial class DeviceNameResolver : IDeviceNameResolver
{
    private readonly Lazy<SmbiosData?> _smbios;

    public DeviceNameResolver(Func<SmbiosData?> smbios)
    {
        _smbios = new Lazy<SmbiosData?>(() =>
        {
            try { return smbios(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        });
    }

    public string? DisplayName(HardwareDevice device)
    {
        if (device.Identity?.SingletonKey == "system-memory")
        {
            return _smbios.Value is { } d && MemoryTitle(d) is { } title ? title : "System Memory";
        }

        return string.IsNullOrWhiteSpace(device.Name) ? DefaultName(device.Category) : null;
    }

    [GeneratedRegex(@"^(unknown|undefined|not specified|to be filled.*|none|n/?a|0+|f+|manufacturer\d*|partnum\d*|modulepartnumber\d*|\s*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private static string? Clean(string? s) => s is null || Placeholder().IsMatch(s.Trim()) ? null : s.Trim();

    /// <summary>Title for installed memory, or null when SMBIOS lists no populated module.</summary>
    public static string? MemoryTitle(SmbiosData data)
    {
        var modules = data.MemoryDevices.Where(m => m.SizeMegabytes > 0).ToList();
        if (modules.Count == 0) return null;

        var totalMb = modules.Sum(m => m.SizeMegabytes);
        var parts = new List<string> { Size(totalMb) };

        var types = modules.Select(m => m.MemoryType).Where(t => !string.IsNullOrWhiteSpace(t) && !t.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !t.Equals("Other", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        var speeds = modules.Select(m => m.ConfiguredSpeedMts ?? m.SpeedMts).OfType<int>().Where(s => s > 0).Distinct().ToList();
        if (types.Count == 1)
        {
            parts.Add(speeds.Count == 1 ? $"{types[0]}-{speeds[0].ToString(CultureInfo.InvariantCulture)}" : types[0]);
        }
        else if (speeds.Count == 1)
        {
            parts.Add($"{speeds[0].ToString(CultureInfo.InvariantCulture)} MT/s");
        }

        var sizes = modules.Select(m => m.SizeMegabytes).Distinct().ToList();
        var layout = sizes.Count == 1 ? $"({modules.Count} × {Size(sizes[0])})" : $"({string.Join(" + ", modules.Select(m => Size(m.SizeMegabytes)))})";
        var title = $"{string.Join(' ', parts)} {layout}";

        var makers = modules.Select(m => Clean(m.Manufacturer)).Distinct().ToList();
        var partNumbers = modules.Select(m => Clean(m.PartNumber)).Distinct().ToList();
        var identity = string.Join(' ', new[] { makers.Count == 1 ? makers[0] : null, partNumbers.Count == 1 ? partNumbers[0] : null }.OfType<string>());
        return identity.Length > 0 ? $"{title} · {identity}" : title;
    }

    private static string Size(long megabytes) => megabytes >= 1024
        ? (megabytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
        : megabytes.ToString(CultureInfo.InvariantCulture) + " MB";

    public static string DefaultName(HardwareCategory category) => category switch
    {
        HardwareCategory.Cpu => "Processor",
        HardwareCategory.Gpu => "Graphics card",
        HardwareCategory.Memory or HardwareCategory.MemoryModule => "Memory",
        HardwareCategory.Motherboard => "Motherboard",
        HardwareCategory.SuperIo => "Super I/O",
        HardwareCategory.EmbeddedController => "Embedded controller",
        HardwareCategory.Storage => "Drive",
        HardwareCategory.Network => "Network adapter",
        HardwareCategory.Cooler => "Cooler",
        HardwareCategory.PowerSupply => "Power supply",
        HardwareCategory.Battery => "Battery",
        _ => "Device",
    };
}
