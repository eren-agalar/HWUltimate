using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Hardware;

namespace NovaHardware.Infrastructure.Smbios;

/// <summary>Reads the firmware SMBIOS table via <c>GetSystemFirmwareTable</c> (no admin rights required).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class SmbiosDiscoveryService : IDeviceDiscoveryService
{
    private const uint Rsmb = 0x52534D42; // 'RSMB'

    private readonly ILogger<SmbiosDiscoveryService> _logger;

    public SmbiosDiscoveryService(ILogger<SmbiosDiscoveryService> logger)
    {
        _logger = logger;
    }

    public string SourceName => "SMBIOS";

    public SmbiosData? LastData { get; private set; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint providerSignature, uint tableId, [Out] byte[]? buffer, uint bufferSize);

    public SmbiosData? ReadTable()
    {
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0)
        {
            _logger.LogWarning("GetSystemFirmwareTable returned 0 (error {Error})", Marshal.GetLastPInvokeError());
            return null;
        }

        var buffer = new byte[size];
        if (GetSystemFirmwareTable(Rsmb, 0, buffer, size) != size)
        {
            return null;
        }

        return LastData = SmbiosParser.Parse(buffer);
    }

    public Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DeviceInfo>>(() =>
    {
        try
        {
            return ReadTable() is { } data ? ToDevices(data) : [];
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogWarning(ex, "SMBIOS table could not be parsed");
            return [];
        }
    }, cancellationToken);

    internal static IReadOnlyList<DeviceInfo> ToDevices(SmbiosData d)
    {
        var list = new List<DeviceInfo>();
        if (d.Baseboard is { } b)
        {
            list.Add(new DeviceInfo
            {
                Id = "smbios:baseboard",
                Name = Name(b.Manufacturer, b.Product) ?? "Motherboard",
                Category = HardwareCategory.Motherboard,
                Manufacturer = b.Manufacturer,
                Model = b.Product,
                SerialNumber = b.SerialNumber,
                Source = "SMBIOS",
                Properties = Props(("Revision", b.Version), ("SMBIOS version", d.Version.ToString()),
                    ("BIOS vendor", d.Bios?.Vendor), ("BIOS version", d.Bios?.Version), ("BIOS date", d.Bios?.ReleaseDate),
                    ("BIOS ROM size", d.Bios?.RomSizeKb is { } kb ? $"{kb} KB" : null)),
            });
        }

        if (d.System is { } s)
        {
            list.Add(new DeviceInfo
            {
                Id = "smbios:system",
                Name = Name(s.Manufacturer, s.Product) ?? "System",
                Category = HardwareCategory.System,
                Manufacturer = s.Manufacturer,
                Model = s.Product,
                SerialNumber = s.SerialNumber,
                Source = "SMBIOS",
                Properties = Props(("Version", s.Version), ("Family", s.Family), ("SKU", s.Sku), ("UUID", s.Uuid?.ToString())),
            });
        }

        foreach (var (cpu, i) in d.Processors.Select((c, i) => (c, i)))
        {
            if (cpu.Version is null)
            {
                continue; // empty socket
            }

            list.Add(new DeviceInfo
            {
                Id = $"smbios:cpu{i}",
                Name = cpu.Version,
                Category = HardwareCategory.Cpu,
                Manufacturer = cpu.Manufacturer,
                Source = "SMBIOS",
                Properties = Props(("Socket", cpu.Socket), ("Cores", cpu.Cores?.ToString()), ("Threads", cpu.Threads?.ToString()),
                    ("External clock", Mhz(cpu.ExternalClockMhz)), ("Max speed", Mhz(cpu.MaxSpeedMhz)), ("Speed at boot", Mhz(cpu.CurrentSpeedMhz))),
            });
        }

        foreach (var (m, i) in d.MemoryDevices.Select((m, i) => (m, i)))
        {
            list.Add(new DeviceInfo
            {
                Id = $"smbios:mem{i}",
                Name = MemoryName(m.Manufacturer, m.PartNumber, m.DeviceLocator),
                Category = HardwareCategory.Memory,
                Manufacturer = m.Manufacturer,
                Model = m.PartNumber,
                SerialNumber = m.SerialNumber,
                Source = "SMBIOS",
                Properties = Props(("Capacity", m.SizeMegabytes >= 1024 ? $"{m.SizeMegabytes / 1024.0:0.#} GB" : $"{m.SizeMegabytes} MB"),
                    ("Type", m.MemoryType), ("Form factor", m.FormFactor), ("Slot", m.DeviceLocator), ("Bank", m.BankLocator),
                    ("Rated speed", m.SpeedMts is { } sp ? $"{sp} MT/s" : null),
                    ("Configured speed", m.ConfiguredSpeedMts is { } cs ? $"{cs} MT/s" : null),
                    ("Configured voltage", m.ConfiguredVoltageMv is { } mv ? $"{mv / 1000.0:0.000} V" : null)),
            });
        }

        return list;
    }

    internal static string MemoryName(string? manufacturer, string? part, string? locator)
    {
        var name = string.Join(' ', new[] { manufacturer, part }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (name.Length == 0) name = "Memory module";
        return string.IsNullOrWhiteSpace(locator) ? name : $"{name} ({locator})";
    }

    private static string? Name(string? a, string? b) =>
        string.Join(' ', new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : null;

    private static string? Mhz(int? v) => v is { } x ? $"{x} MHz" : null;

    internal static IReadOnlyList<HardwareProperty> Props(params (string Name, string? Value)[] items) =>
        items.Where(i => !string.IsNullOrWhiteSpace(i.Value)).Select(i => new HardwareProperty(i.Name, i.Value!)).ToList();
}
