using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Hardware;
using S = HWUltimate.Domain.Hardware.PropertySections;

namespace HWUltimate.Hardware.Nvidia;

/// <summary>
/// NVIDIA identification details from NVML and NVAPI (architecture, VBIOS, memory bus width, clocks, power limits,
/// PCIe capability, temperature thresholds). Merged with the vendor-neutral GPU entry by device name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvidiaGpuDetailSource : IDeviceDiscoveryService
{
    private readonly ILogger<NvidiaGpuDetailSource> _logger;

    public NvidiaGpuDetailSource(ILogger<NvidiaGpuDetailSource> logger)
    {
        _logger = logger;
    }

    public string SourceName => "NVML/NVAPI";

    public Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DeviceInfo>>(() =>
    {
        using var nvml = NvmlNative.TryLoad(out _);
        using var nvapi = NvApiNative.TryLoad(out _);
        if (nvml is null) return [];
        try
        {
            var nvapiByBus = new Dictionary<uint, nint>();
            foreach (var h in nvapi?.EnumPhysicalGpus() ?? [])
            {
                if (nvapi!.GetBusId(h) is { } bus) nvapiByBus[bus] = h;
            }

            var cuda = nvml.CudaDriverVersion();
            return nvml.Devices().Select((d, i) =>
            {
                var pci = nvml.Pci(d);
                nint? nvapiHandle = pci is { } p && nvapiByBus.TryGetValue(p.Bus, out var nh) ? nh : null;
                return Build(nvml, nvapi, d, nvapiHandle, i, cuda);
            }).ToList();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.SEHException)
        {
            _logger.LogWarning(ex, "NVML detail query failed");
            return [];
        }
    }, cancellationToken);

    private static DeviceInfo Build(NvmlNative nvml, NvApiNative? nvapi, nint d, nint? nvapiHandle, int index, int? cuda)
    {
        const string Nvml = "NVML";
        const string Nvapi = "NVAPI";
        var props = new List<HardwareProperty>();
        void Add(string section, string name, string? value, string source)
        {
            if (!string.IsNullOrWhiteSpace(value)) props.Add(new HardwareProperty(name, value, section, source));
        }

        void Na(string section, string name, string note, string src) => props.Add(HardwareProperty.Unavailable(name, section, note, src));
        static string W(uint mw) => $"{mw / 1000.0:0.#} W";

        var name = nvml.Name(d) ?? $"NVIDIA GPU #{index}";
        Add(S.Identification, "Architecture", NvmlDecode.Architecture(nvml.Architecture(d)), Nvml);
        Add(S.Identification, "Board part number", nvml.BoardPartNumber(d), Nvml);
        Add(S.Identification, "GPU UUID", nvml.Uuid(d), Nvml);
        if (nvml.Pci(d) is { } pci)
        {
            Add(S.Advanced, "PCI domain / bus / device", $"{pci.Domain:X4}:{pci.Bus:X2}:{pci.Device:X2}", Nvml);
        }

        Add(S.Specifications, "CUDA cores", nvml.CoreCount(d)?.ToString(System.Globalization.CultureInfo.InvariantCulture), Nvml);
        if (nvml.MemoryBusWidth(d) is { } bw) Add(S.Specifications, "Memory bus width", $"{bw}-bit", Nvml);
        else Na(S.Specifications, "Memory bus width", "Not reported by this driver / GPU.", Nvml);
        if (nvml.Memory(d) is { } mem) Add(S.Specifications, "VRAM (usable, NVML)", $"{mem.Total / 1024d / 1024 / 1024:0.##} GB", Nvml);
        Na(S.Specifications, "Memory bandwidth", "Not computed: it requires the memory type's data-rate multiplier, which no public NVIDIA API reports.", Nvml);

        // Clocks: NVAPI base / boost are the board's specified frequencies; NVML max clocks are the highest the GPU may reach.
        if (nvapi is not null && nvapiHandle is { } h)
        {
            if (nvapi.TryGetClocks(h, 1, out var baseClk) && baseClk.IsPresent(NvClockFrequencies.Graphics))
                Add(S.Performance, "Base core clock", $"{baseClk.FrequencyKhz(NvClockFrequencies.Graphics) / 1000} MHz", Nvapi);
            if (nvapi.TryGetClocks(h, 2, out var boostClk) && boostClk.IsPresent(NvClockFrequencies.Graphics))
                Add(S.Performance, "Boost core clock", $"{boostClk.FrequencyKhz(NvClockFrequencies.Graphics) / 1000} MHz", Nvapi);
            if (nvapi.TryGetClocks(h, 1, out var baseMem) && baseMem.IsPresent(NvClockFrequencies.Memory))
                Add(S.Performance, "Memory clock (base)", $"{baseMem.FrequencyKhz(NvClockFrequencies.Memory) / 1000} MHz", Nvapi);
        }

        if (nvml.MaxClockMhz(d, 0) is { } maxG) Add(S.Performance, "Maximum graphics clock", $"{maxG} MHz", Nvml);
        if (nvml.MaxClockMhz(d, 2) is { } maxM) Add(S.Performance, "Maximum memory clock", $"{maxM} MHz", Nvml);
        if (nvml.ClockMhz(d, 0, 2) is { } appDef) Add(S.Performance, "Default application clock", $"{appDef} MHz", Nvml);

        if (nvml.EnforcedPowerLimitMw(d) is { } enf) Add(S.Performance, "Current power limit (enforced)", W(enf), Nvml);
        else Na(S.Performance, "Current power limit", "Power management is not supported or not exposed for this GPU.", Nvml);
        if (nvml.DefaultPowerLimitMw(d) is { } def) Add(S.Performance, "Default power limit", W(def), Nvml);
        if (nvml.PowerLimitConstraintsMw(d) is { } c)
        {
            Add(S.Performance, "Minimum power limit", W(c.Min), Nvml);
            Add(S.Performance, "Maximum power limit", W(c.Max), Nvml);
        }

        if (nvml.CurrentPcieGeneration(d) is { } cg) Add(S.Performance, "PCIe generation (current)", $"Gen {cg} x{nvml.CurrentPcieWidth(d)}", Nvml);
        if (nvml.MaxPcieGeneration(d) is { } mg) Add(S.Performance, "PCIe generation (max, system)", $"Gen {mg} x{nvml.MaxPcieWidth(d)}", Nvml);
        if (nvml.GpuMaxPcieGeneration(d) is { } gg) Add(S.Performance, "PCIe generation (max, GPU)", $"Gen {gg}", Nvml);
        if (nvml.TemperatureThreshold(d, 1) is { } slow) Add(S.Performance, "Thermal slowdown threshold", $"{slow} °C", Nvml);
        if (nvml.TemperatureThreshold(d, 0) is { } shut) Add(S.Performance, "Thermal shutdown threshold", $"{shut} °C", Nvml);
        if (nvml.TemperatureThreshold(d, 3) is { } gmax) Add(S.Performance, "Maximum operating temperature", $"{gmax} °C", Nvml);

        Add(S.Firmware, "VBIOS version", nvml.Vbios(d), Nvml);
        Add(S.Firmware, "Driver version", nvml.DriverVersion(), Nvml);
        Add(S.Firmware, "NVML version", nvml.NvmlVersion(), Nvml);
        if (cuda is { } cv) Add(S.Firmware, "CUDA driver API", NvmlDecode.CudaVersion(cv), Nvml);
        if (nvml.DriverModel(d) is { } dm) Add(S.Firmware, "Driver model", NvmlDecode.DriverModel(dm.Current), Nvml);

        Na(S.Advanced, "ASIC quality (NVIDIA)", "NVAPI and NVML do not expose an ASIC-quality / leakage value for NVIDIA GPUs. HWUltimate does not estimate it.", "NVAPI, NVML");

        var key = nvml.Pci(d) is { } loc ? $"pci:{new PciLocation((int)loc.Domain, (int)loc.Bus, (int)loc.Device, 0)}" : null;
        return new DeviceInfo { Id = $"nvml:gpu{index}", Name = name, Category = HardwareCategory.Gpu, Manufacturer = "NVIDIA", Source = Nvml, Properties = props, PhysicalKey = key };
    }
}
