using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using HWUltimate.Core.Abstractions;
using HWUltimate.Core.HardwareInfo;
using HWUltimate.Domain.Hardware;
using S = HWUltimate.Domain.Hardware.PropertySections;

namespace HWUltimate.Infrastructure.Gpu;

/// <summary>
/// Vendor-neutral GPU identification: WMI (name, driver), PnP / PCI (ids, bus location, PCIe link), the display driver's
/// registry key (VRAM size, VBIOS string, chip / DAC type), DXGI (memory, Direct3D feature levels) and Vulkan.
/// Vendor SDK details (NVML / NVAPI) are contributed by the vendor provider projects and merged by device name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuDiscoveryService : IDeviceDiscoveryService
{
    private const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\";
    private readonly ILogger<GpuDiscoveryService> _logger;

    public GpuDiscoveryService(ILogger<GpuDiscoveryService> logger)
    {
        _logger = logger;
    }

    public string SourceName => "GPU (WMI/PCI/DXGI/Vulkan)";

    private sealed record WmiGpu(string Name, string? PnpId, string? Driver, string? DriverDate, string? Compat, string? VideoProcessor, string? Status, string? Mode);

    public Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DeviceInfo>>(() =>
    {
        var gpus = ReadWmi();
        if (gpus.Count == 0) return [];

        IReadOnlyDictionary<string, PciDeviceProperties> pci = new Dictionary<string, PciDeviceProperties>();
        IReadOnlyList<DxgiAdapterInfo> dxgi = [];
        IReadOnlyList<VulkanDeviceInfo>? vulkan = null;
        string? vulkanError = null;
        try { pci = PnpDeviceProperties.ReadPciDevices(); } catch (Exception ex) when (IsInterop(ex)) { _logger.LogDebug(ex, "PnP properties unavailable"); }
        try { dxgi = DxgiNative.Enumerate(); } catch (Exception ex) when (IsInterop(ex)) { _logger.LogDebug(ex, "DXGI unavailable"); }
        try { vulkan = VulkanNative.Enumerate(out vulkanError); } catch (Exception ex) when (IsInterop(ex)) { vulkanError = ex.Message; }

        return gpus.Select((g, i) => Build(g, i, pci, dxgi, vulkan, vulkanError)).ToList();
    }, cancellationToken);

    private static bool IsInterop(Exception ex) => ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.SEHException or System.Runtime.InteropServices.COMException;

    private List<WmiGpu> ReadWmi()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion, DriverDate, AdapterCompatibility, VideoProcessor, Status, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
            return s.Get().Cast<ManagementBaseObject>()
                .Select(o => new WmiGpu(o["Name"]?.ToString()?.Trim() ?? "Display adapter", o["PNPDeviceID"]?.ToString(), o["DriverVersion"]?.ToString(),
                    o["DriverDate"]?.ToString(), o["AdapterCompatibility"]?.ToString(), o["VideoProcessor"]?.ToString(), o["Status"]?.ToString(),
                    o["CurrentHorizontalResolution"] is uint w && w > 0 ? $"{w} × {o["CurrentVerticalResolution"]} @ {o["CurrentRefreshRate"]} Hz" : null))
                .ToList();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(ex, "Win32_VideoController query failed");
            return [];
        }
    }

    private DeviceInfo Build(WmiGpu g, int index, IReadOnlyDictionary<string, PciDeviceProperties> pciMap,
        IReadOnlyList<DxgiAdapterInfo> dxgiList, IReadOnlyList<VulkanDeviceInfo>? vulkan, string? vulkanError)
    {
        var props = new List<HardwareProperty>();
        void Add(string section, string name, string? value, string source)
        {
            if (!string.IsNullOrWhiteSpace(value)) props.Add(new HardwareProperty(name, value.Trim(), section, source));
        }

        void Na(string section, string name, string note, string? checkedSources = null) => props.Add(HardwareProperty.Unavailable(name, section, note, checkedSources));

        var pciId = PciIdentity.Parse(g.PnpId);
        pciMap.TryGetValue(g.PnpId ?? string.Empty, out var pci);
        var dxgi = pciId is null ? null : dxgiList.FirstOrDefault(d => !d.IsSoftware && d.VendorId == pciId.VendorId && d.DeviceId == pciId.DeviceId
                                                                      && (pciId.SubsystemId is null || d.SubSysId == (uint)((pciId.SubsystemId.Value << 16) | pciId.SubsystemVendorId!.Value)));
        var vk = pciId is null ? null : vulkan?.FirstOrDefault(v => v.VendorId == pciId.VendorId && v.DeviceId == pciId.DeviceId);
        var reg = ReadDriverKey(pci?.DriverKey);
        var chip = pciId is null ? null : GpuChipCatalog.Find(pciId.VendorId, pciId.DeviceId);

        // ---------------- Overview
        Add(S.Overview, "GPU", g.Name, "WMI Win32_VideoController");
        Add(S.Overview, "Manufacturer", pciId?.VendorName ?? g.Compat, pciId is not null ? "PCI vendor id" : "WMI");
        Add(S.Overview, "Status", g.Status, "WMI");
        Add(S.Performance, "Current display mode", g.Mode, "WMI Win32_VideoController");

        // ---------------- Identification
        if (pciId is not null)
        {
            Add(S.Identification, "PCI vendor ID", $"0x{pciId.VendorId:X4} ({pciId.VendorName})", "PnP device id");
            Add(S.Identification, "PCI device ID", $"0x{pciId.DeviceId:X4}", "PnP device id");
            Add(S.Identification, "PCI subsystem ID", pciId.SubsystemText, "PnP device id");
            Add(S.Identification, "Board partner", pciId.SubsystemVendorId is { } sv ? PciIdentity.VendorNameOf(sv) : null, "PCI subsystem vendor id");
            Add(S.Identification, "GPU revision", pciId.Revision is { } rev ? $"0x{rev:X2}" : null, "PnP device id");
        }

        Add(S.Identification, "Chip type (driver)", reg.ChipType, "Display driver registry");
        Add(S.Identification, "DAC type (driver)", reg.DacType, "Display driver registry");
        if (chip is not null)
        {
            Add(S.Identification, "GPU chip", chip.Chip, GpuChipCatalog.SourceLabel);
            Add(S.Identification, "Architecture", chip.Architecture, GpuChipCatalog.SourceLabel);
        }
        else
        {
            Na(S.Identification, "GPU chip codename", "Device id is not in HWUltimate's chip catalog and Windows does not expose the codename.", "PCI id, driver registry");
        }

        Add(S.Identification, "PnP device ID", g.PnpId, "WMI");

        // ---------------- Specifications
        var vram = reg.MemorySize ?? (dxgi?.DedicatedVideoMemory > 0 ? dxgi.DedicatedVideoMemory : null);
        Add(S.Specifications, "VRAM capacity", vram is { } v ? $"{v / 1024d / 1024 / 1024:0.##} GB" : null,
            reg.MemorySize is not null ? "Display driver registry (qwMemorySize)" : "DXGI DedicatedVideoMemory");
        if (dxgi is not null)
        {
            Add(S.Specifications, "Shared system memory", $"{dxgi.SharedSystemMemory / 1024d / 1024 / 1024:0.##} GB", "DXGI");
        }

        if (chip is not null)
        {
            Add(S.Specifications, "Manufacturing process", chip.Process, GpuChipCatalog.SourceLabel);
            Add(S.Specifications, "Die size", $"{chip.DieSizeMm2.ToString("0.#", CultureInfo.InvariantCulture)} mm²{(chip.Note is null ? "" : $" — {chip.Note}")}", GpuChipCatalog.SourceLabel);
            Add(S.Specifications, "Transistors", $"{chip.TransistorsBillion.ToString("0.##", CultureInfo.InvariantCulture)} billion", GpuChipCatalog.SourceLabel);
        }
        else
        {
            Na(S.Specifications, "Die size / transistors / process", "Only shown for chips in HWUltimate's catalog of officially published figures.", GpuChipCatalog.SourceLabel);
        }

        Na(S.Specifications, "VRAM type", "Memory type is not exposed by Windows, DXGI, NVML or NVAPI public interfaces.", "DXGI, NVML, NVAPI");

        // ---------------- Performance capabilities
        if (pci is not null)
        {
            Add(S.Performance, "PCIe link (current)", Link(pci.CurrentLinkSpeed, pci.CurrentLinkWidth), "PnP DEVPKEY_PciDevice_CurrentLink*");
            Add(S.Performance, "PCIe link (maximum)", Link(pci.MaxLinkSpeed, pci.MaxLinkWidth), "PnP DEVPKEY_PciDevice_MaxLink*");
            if (PcieLink.Generation(pci.MaxLinkSpeed) is { } gen) Add(S.Performance, "PCIe generation (device max)", $"Gen {gen}", "PnP");
            if (pci.CurrentLinkSpeed is not null && pci.MaxLinkSpeed is not null && pci.CurrentLinkSpeed < pci.MaxLinkSpeed)
            {
                Add(S.Performance, "PCIe link note", "Current link is below maximum — normal at idle (ASPM / power saving) or limited by the slot / CPU.", "HWUltimate");
            }
        }
        else
        {
            Na(S.Performance, "PCIe link", "Windows did not report PCIe link properties for this adapter (e.g. integrated or virtual GPU).", "PnP");
        }

        if (dxgi?.D3D12FeatureLevel is { } fl12)
        {
            Add(S.Performance, "Direct3D 12 feature level", DxgiNative.FeatureLevelText(fl12) + (fl12 >= 0xc200 ? " (DirectX 12 Ultimate class)" : ""), "D3D12CreateDevice probe");
        }
        else
        {
            Na(S.Performance, "Direct3D 12 feature level", "Adapter does not support Direct3D 12 or the runtime is unavailable.", "D3D12CreateDevice");
        }

        if (dxgi?.D3D11FeatureLevel is { } fl11) Add(S.Performance, "Direct3D 11 feature level", DxgiNative.FeatureLevelText(fl11), "D3D11CreateDevice probe");
        if (vk is not null)
        {
            Add(S.Performance, "Vulkan API version", vk.ApiVersionText, "vkGetPhysicalDeviceProperties");
        }
        else
        {
            Na(S.Performance, "Vulkan", vulkanError ?? "No Vulkan physical device matched this adapter.", "Vulkan loader");
        }

        Na(S.Performance, "Supported display outputs", "Windows exposes connected monitors, not the adapter's physical output configuration. Connected displays and their connection types are listed under Display.", "WMI WmiMonitorConnectionParams");

        // ---------------- Firmware & drivers
        Add(S.Firmware, "Driver version", g.Driver, "WMI");
        Add(S.Firmware, "Driver date", g.DriverDate is { } dd ? SafeDate(dd) : null, "WMI");
        Add(S.Firmware, "Driver provider", reg.Provider, "Display driver registry");
        Add(S.Firmware, "VBIOS version", reg.BiosString, "Display driver registry (HardwareInformation.BiosString)");
        Add(S.Firmware, "Adapter string (driver)", reg.AdapterString, "Display driver registry");
        if (vk is not null) Add(S.Firmware, "Vulkan driver version", vk.DriverVersionText, "Vulkan");

        // ---------------- Silicon quality
        Na(S.Advanced, "ASIC / silicon quality",
            "No public NVIDIA (NVAPI, NVML), AMD (ADLX) or Windows interface exposes an ASIC-quality or leakage metric. HWUltimate does not estimate it from temperature, voltage or power.",
            "NVAPI, NVML, ADLX, Windows");

        // ---------------- Advanced
        if (pci is not null)
        {
            Add(S.Advanced, "PCI bus / device / function", pci.BusNumber is { } bus && pci.Device is { } dev && pci.Function is { } fn ? $"{bus} / {dev} / {fn}" : null, "PnP DEVPKEY_Device_BusNumber / Address");
            Add(S.Advanced, "PCI location", pci.LocationInfo, "SetupAPI");
            Add(S.Advanced, "Driver key", pci.DriverKey, "SetupAPI");
        }

        if (dxgi is not null)
        {
            Add(S.Advanced, "DXGI description", dxgi.Description, "DXGI");
            Add(S.Advanced, "DXGI dedicated system memory", $"{dxgi.DedicatedSystemMemory / 1024d / 1024:0} MB", "DXGI");
        }

        var location = pci is not null ? PnpIdentityResolver.Location(pci) : null;
        return new DeviceInfo
        {
            Id = $"gpu:{index}",
            PhysicalKey = location is { } loc ? $"pci:{loc}" : g.PnpId is { } pnp ? $"pnp:{pnp.ToUpperInvariant()}" : null,
            Name = g.Name,
            Category = HardwareCategory.Gpu,
            Manufacturer = pciId?.VendorName ?? g.Compat,
            Model = g.Name,
            DriverVersion = g.Driver,
            Source = "PCI/DXGI",
            Properties = props,
        };
    }

    private static string? Link(uint? speed, uint? width)
    {
        var s = PcieLink.SpeedText(speed);
        var w = PcieLink.WidthText(width);
        return s is null && w is null ? null : $"{w ?? "x?"} @ {s ?? "unknown speed"}";
    }

    internal sealed record DriverRegistry(ulong? MemorySize, string? BiosString, string? ChipType, string? DacType, string? AdapterString, string? Provider);

    private static DriverRegistry ReadDriverKey(string? driverKey)
    {
        if (string.IsNullOrEmpty(driverKey)) return new(null, null, null, null, null, null);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ClassRoot + driverKey);
            if (key is null) return new(null, null, null, null, null, null);
            ulong? mem = key.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long l => (ulong)l,
                byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b),
                _ => key.GetValue("HardwareInformation.MemorySize") switch { int i => (uint)i, byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b), _ => null },
            };
            return new DriverRegistry(mem, RegText(key, "HardwareInformation.BiosString"), RegText(key, "HardwareInformation.ChipType"),
                RegText(key, "HardwareInformation.DacType"), RegText(key, "HardwareInformation.AdapterString"), RegText(key, "ProviderName"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new(null, null, null, null, null, null);
        }
    }

    /// <summary>Driver HardwareInformation values may be REG_SZ or REG_BINARY UTF-16 strings.</summary>
    internal static string? RegText(RegistryKey key, string name) => key.GetValue(name) switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => s.Trim('\0', ' '),
        byte[] b when b.Length >= 2 => Encoding.Unicode.GetString(b).Trim('\0', ' ') is { Length: > 0 } t ? t : null,
        string[] arr => arr.FirstOrDefault(),
        _ => null,
    };

    private static string? SafeDate(string wmi)
    {
        try { return ManagementDateTimeConverter.ToDateTime(wmi).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
