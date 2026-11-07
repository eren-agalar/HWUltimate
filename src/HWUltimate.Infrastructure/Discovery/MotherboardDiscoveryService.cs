using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Hardware;
using HWUltimate.Infrastructure.Gpu;
using HWUltimate.Infrastructure.Smbios;
using S = HWUltimate.Domain.Hardware.PropertySections;

namespace HWUltimate.Infrastructure.Discovery;

/// <summary>
/// Motherboard details beyond SMBIOS: chipset (identified through its PCI LPC/ISA bridge function), PCI host bridge,
/// and Super I/O / embedded-controller chips detected by the sensor provider.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MotherboardDiscoveryService : IDeviceDiscoveryService
{
    private readonly SmbiosDiscoveryService _smbios;
    private readonly ISensorPollingService _polling;
    private readonly ILogger<MotherboardDiscoveryService> _logger;

    public MotherboardDiscoveryService(SmbiosDiscoveryService smbios, ISensorPollingService polling, ILogger<MotherboardDiscoveryService> logger)
    {
        _smbios = smbios;
        _polling = polling;
        _logger = logger;
    }

    public string SourceName => "Motherboard";

    public Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DeviceInfo>>(() =>
    {
        var data = _smbios.LastData ?? SafeRead();
        if (data?.Baseboard is not { } b) return [];
        var name = string.Join(' ', new[] { b.Manufacturer, b.Product }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (name.Length == 0) name = "Motherboard";

        IReadOnlyList<(string Name, string InstanceId)> lpc = [], host = [];
        try
        {
            lpc = PnpDeviceProperties.FindByClassCode("CC_0601");
            host = PnpDeviceProperties.FindByClassCode("CC_0600");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "SetupAPI unavailable");
        }

        var sensorDevices = _polling.Current.Devices;
        return [Build(name, lpc, host, sensorDevices, data.Version)];
    }, cancellationToken);

    private SmbiosData? SafeRead()
    {
        try { return _smbios.ReadTable(); }
        catch (Exception ex) when (ex is FormatException or DllNotFoundException) { return null; }
    }

    internal static DeviceInfo Build(string boardName, IReadOnlyList<(string Name, string InstanceId)> lpc, IReadOnlyList<(string Name, string InstanceId)> host,
        IReadOnlyList<HardwareDevice> sensorDevices, Version smbiosVersion)
    {
        var props = new List<HardwareProperty>();
        if (lpc.Count > 0)
        {
            props.Add(new("Chipset (LPC / ISA bridge)", string.Join("; ", lpc.Select(l => l.Name).Distinct()), S.Overview, "SetupAPI, PCI class 06-01",
                Note: "Identified through the chipset's LPC/eSPI bridge function. Some AMD platforms report a generic bridge name."));
            var pci = Core.HardwareInfo.PciIdentity.Parse(lpc[0].InstanceId);
            if (pci is not null) props.Add(new("Chipset PCI ID", $"{pci.VendorId:X4}:{pci.DeviceId:X4} ({pci.VendorName})", S.Identification, "PnP device id"));
        }
        else
        {
            props.Add(HardwareProperty.Unavailable("Chipset", S.Overview, "No PCI LPC/ISA bridge function was reported by Windows.", "SetupAPI"));
        }

        if (host.Count > 0) props.Add(new("PCI host bridge", string.Join("; ", host.Select(h => h.Name).Distinct()), S.Specifications, "SetupAPI, PCI class 06-00"));

        var superIo = sensorDevices.Where(d => d.Category == HardwareCategory.SuperIo).Select(d => d.Name).Distinct().ToList();
        var ec = sensorDevices.Where(d => d.Category == HardwareCategory.EmbeddedController).Select(d => d.Name).Distinct().ToList();
        if (superIo.Count > 0) props.Add(new("Super I/O / hardware monitor", string.Join("; ", superIo), S.Specifications, "LibreHardwareMonitor"));
        else props.Add(HardwareProperty.Unavailable("Super I/O / hardware monitor", S.Specifications, "No Super I/O chip detected. Detection requires administrator rights.", "LibreHardwareMonitor"));
        if (ec.Count > 0) props.Add(new("Embedded controller", string.Join("; ", ec), S.Advanced, "LibreHardwareMonitor"));
        else props.Add(HardwareProperty.Unavailable("Embedded controller", S.Advanced, "No supported embedded controller was detected (only specific board models are supported).", "LibreHardwareMonitor"));

        props.Add(new("SMBIOS version", smbiosVersion.ToString(), S.Firmware, "SMBIOS entry point"));
        props.Add(HardwareProperty.Unavailable("PCIe slot configuration", S.Specifications,
            "Slot generation and lane wiring are not described reliably by SMBIOS type 9 on most boards; per-device link speed is shown on each PCIe device.", "SMBIOS type 9"));

        return new DeviceInfo { Id = "board:detail", Name = boardName, Category = HardwareCategory.Motherboard, Source = "SetupAPI", Properties = props };
    }
}
