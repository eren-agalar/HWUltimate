using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Hardware.Nvidia;

/// <summary>
/// NVIDIA-specific telemetry. NVAPI supplies temperatures by target, engine utilisation, clocks and fan tach;
/// NVML adds board power, power limits, performance-limit (clock event) flags, VRAM usage, PCIe link state and P-state.
/// NVML devices are matched to NVAPI GPUs by PCI bus number.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvApiProvider : IHardwareProvider
{
    public const string ProviderName = "NVIDIA (NVAPI/NVML)";

    private static readonly string[] UtilizationDomains = ["GPU Core", "Memory Controller", "Video Engine", "Bus Interface"];

    private readonly ILogger<NvApiProvider> _logger;
    private readonly Lock _gate = new();
    private NvApiNative? _api;
    private NvmlNative? _nvml;
    private Gpu[] _gpus = [];
    private List<HardwareDevice> _devices = [];
    private List<RawSensorReading> _readings = [];

    private sealed record Gpu(nint? NvApi, nint? Nvml, string Id, string Name, DeviceIdentity? Identity = null);

    public NvApiProvider(ILogger<NvApiProvider> logger)
    {
        _logger = logger;
    }

    public string Name => ProviderName;
    public int Priority => 10;
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;
    public string? DriverVersion { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _api = NvApiNative.TryLoad(out var nvapiReason);
            _nvml = NvmlNative.TryLoad(out var nvmlReason);
            if (_api is null && _nvml is null)
            {
                Status = ProviderStatus.Unavailable(nvapiReason);
                return Task.CompletedTask;
            }

            var nvmlByBus = new Dictionary<uint, nint>();
            var nvmlHandles = _nvml?.Devices() ?? [];
            foreach (var h in nvmlHandles)
            {
                if (_nvml!.Pci(h) is { } pci) nvmlByBus[pci.Bus] = h;
            }

            var gpus = new List<Gpu>();
            var nvapiHandles = _api?.EnumPhysicalGpus() ?? [];
            for (var i = 0; i < nvapiHandles.Length; i++)
            {
                var h = nvapiHandles[i];
                nint? nvml = _api!.GetBusId(h) is { } bus && nvmlByBus.TryGetValue(bus, out var m) ? m : nvmlHandles.Length == nvapiHandles.Length ? nvmlHandles[i] : null;
                gpus.Add(new Gpu(h, nvml, $"nvapi:gpu{i}", _api.GetFullName(h) ?? (nvml is { } n ? _nvml!.Name(n) : null) ?? $"NVIDIA GPU #{i}"));
            }

            if (gpus.Count == 0)
            {
                gpus.AddRange(nvmlHandles.Select((h, i) => new Gpu(null, h, $"nvapi:gpu{i}", _nvml!.Name(h) ?? $"NVIDIA GPU #{i}")));
            }

            _gpus = gpus.Select(g => g with { Identity = BuildIdentity(g) }).ToArray();
            DriverVersion = _api?.GetDriverVersion() ?? _nvml?.DriverVersion();
            _devices = _gpus.Select(g => new HardwareDevice(g.Id, g.Name, HardwareCategory.Gpu, ProviderName, Vendor: "NVIDIA", Identity: g.Identity)).ToList();
            var parts = new List<string> { $"{_gpus.Length} GPU(s)", $"driver {DriverVersion ?? "unknown"}" };
            if (_api is null) parts.Add("NVAPI unavailable");
            if (_nvml is null) parts.Add($"NVML unavailable: {nvmlReason}");
            Status = _gpus.Length == 0 ? ProviderStatus.Unavailable("No NVIDIA GPUs enumerated.") : ProviderStatus.Ready(string.Join(", ", parts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NVIDIA provider initialisation failed");
            Status = ProviderStatus.Unavailable($"NVIDIA API error: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void Update()
    {
        lock (_gate)
        {
            var list = new List<RawSensorReading>();
            foreach (var g in _gpus)
            {
                if (_api is not null && g.NvApi is { } h)
                {
                    ReadThermals(_api, h, g.Id, list);
                    ReadUtilization(_api, h, g.Id, list);
                    ReadClocks(_api, h, g.Id, list);
                    if (_api.GetTachRpm(h) is { } rpm)
                    {
                        list.Add(Reading(g.Id, "fan/0", "GPU Fan", SensorType.FanSpeed, SensorUnit.Rpm, rpm));
                    }
                }

                if (_nvml is not null && g.Nvml is { } n)
                {
                    ReadNvml(_nvml, n, g.Id, list);
                }
            }

            _readings = list;
        }
    }

    /// <summary>PCI identity: NVML reports segment / bus / device and ids; NVAPI reports bus, slot and ids.</summary>
    private DeviceIdentity BuildIdentity(Gpu g)
    {
        PciLocation? location = null;
        ushort? device = null, subVendor = null, subDevice = null;
        if (_nvml is not null && g.Nvml is { } n && _nvml.Pci(n) is { } p)
        {
            location = new PciLocation((int)p.Domain, (int)p.Bus, (int)p.Device, 0);
            device = (ushort)(p.PciDeviceId >> 16);
            subDevice = (ushort)(p.PciSubSystemId >> 16);
            subVendor = (ushort)(p.PciSubSystemId & 0xFFFF);
        }
        else if (_api is not null && g.NvApi is { } h && _api.GetBusId(h) is { } bus)
        {
            location = new PciLocation(0, (int)bus, (int)(_api.GetBusSlotId(h) ?? 0), 0);
        }

        if (_api is not null && g.NvApi is { } hh && _api.GetPciIdentifiers(hh) is { } ids)
        {
            device ??= (ushort)(ids.DeviceId >> 16);
            subDevice ??= (ushort)(ids.SubSystemId >> 16);
            subVendor ??= (ushort)(ids.SubSystemId & 0xFFFF);
        }

        return new DeviceIdentity
        {
            Location = location,
            VendorId = 0x10DE,
            DeviceId = device,
            SubsystemVendorId = subVendor is 0 ? null : subVendor,
            SubsystemId = subDevice is 0 ? null : subDevice,
        };
    }

    private static void ReadNvml(NvmlNative nvml, nint d, string id, List<RawSensorReading> list)
    {
        if (nvml.PowerUsageMw(d) is { } p) list.Add(Reading(id, "nvml/power", "GPU Board Power", SensorType.Power, SensorUnit.Watt, p / 1000.0));
        if (nvml.EnforcedPowerLimitMw(d) is { } el) list.Add(Reading(id, "nvml/powerlimit/enforced", "Power Limit (enforced)", SensorType.Power, SensorUnit.Watt, el / 1000.0));
        if (nvml.PowerLimitMw(d) is { } pl) list.Add(Reading(id, "nvml/powerlimit/current", "Power Limit (management)", SensorType.Power, SensorUnit.Watt, pl / 1000.0));

        if (nvml.ClockEventReasons(d) is { } mask)
        {
            var i = 0;
            foreach (var (bit, name) in NvmlDecode.ClockEventReasons)
            {
                list.Add(Reading(id, $"nvml/limit/{bit:X}", name, SensorType.PerformanceLimit, SensorUnit.Flag, (mask & bit) != 0 ? 1 : 0, i++));
            }
        }

        if (nvml.Memory(d) is { } m)
        {
            const double mb = 1024d * 1024;
            list.Add(Reading(id, "nvml/vram/used", "VRAM Used", SensorType.MemorySmall, SensorUnit.Megabyte, m.Used / mb));
            list.Add(Reading(id, "nvml/vram/free", "VRAM Free", SensorType.MemorySmall, SensorUnit.Megabyte, m.Free / mb, 1));
            list.Add(Reading(id, "nvml/vram/total", "VRAM Total", SensorType.MemorySmall, SensorUnit.Megabyte, m.Total / mb, 2));
            if (m.Total > 0) list.Add(Reading(id, "nvml/vram/load", "VRAM Usage", SensorType.Utilization, SensorUnit.Percent, 100.0 * m.Used / m.Total, 9));
        }

        if (nvml.CurrentPcieGeneration(d) is { } gen) list.Add(Reading(id, "nvml/pcie/gen", "PCIe Link Generation", SensorType.Other, SensorUnit.None, gen));
        if (nvml.CurrentPcieWidth(d) is { } w) list.Add(Reading(id, "nvml/pcie/width", "PCIe Link Width", SensorType.Other, SensorUnit.None, w, 1));
        if (nvml.PcieThroughputKBps(d, 1) is { } rx) list.Add(Reading(id, "nvml/pcie/rx", "PCIe Receive", SensorType.Throughput, SensorUnit.BytesPerSecond, rx * 1024.0));
        if (nvml.PcieThroughputKBps(d, 0) is { } tx) list.Add(Reading(id, "nvml/pcie/tx", "PCIe Transmit", SensorType.Throughput, SensorUnit.BytesPerSecond, tx * 1024.0, 1));
        if (nvml.PerformanceState(d) is { } ps) list.Add(Reading(id, "nvml/pstate", "Performance State (P-state)", SensorType.Other, SensorUnit.None, ps, 2));

        // Driver-reported thermal limits: exposed as metadata (never as live temperatures).
        if (nvml.TemperatureThreshold(d, 1) is { } slow and > 0)
            list.Add(Reading(id, "nvml/threshold/slowdown", "GPU Slowdown Temperature Threshold", SensorType.Temperature, SensorUnit.Celsius, slow, 10, SensorKind.Metadata));
        if (nvml.TemperatureThreshold(d, 0) is { } shut and > 0)
            list.Add(Reading(id, "nvml/threshold/shutdown", "GPU Shutdown Temperature Threshold", SensorType.Temperature, SensorUnit.Celsius, shut, 11, SensorKind.Metadata));
    }

    private static void ReadThermals(NvApiNative api, nint handle, string id, List<RawSensorReading> list)
    {
        if (!api.TryGetThermal(handle, out var thermal)) return;
        var count = (int)Math.Min(thermal.Count, NvThermalSettings.MaxSensors);
        for (var i = 0; i < count; i++)
        {
            var s = thermal[i];
            var name = s.Target switch
            {
                NvThermalTarget.Gpu => "GPU Core",
                NvThermalTarget.Memory => "GPU Memory",
                NvThermalTarget.PowerSupply => "GPU Power Supply",
                NvThermalTarget.Board => "GPU Board",
                _ => $"GPU Sensor {i}",
            };
            list.Add(Reading(id, $"temperature/{i}", name, SensorType.Temperature, SensorUnit.Celsius, s.CurrentTemp, i));
        }
    }

    private static void ReadUtilization(NvApiNative api, nint handle, string id, List<RawSensorReading> list)
    {
        if (!api.TryGetUtilization(handle, out var info)) return;
        for (var d = 0; d < UtilizationDomains.Length; d++)
        {
            if (info.IsPresent(d))
            {
                list.Add(Reading(id, $"load/{d}", UtilizationDomains[d], SensorType.Utilization, SensorUnit.Percent, info.Percentage(d), d));
            }
        }
    }

    private static void ReadClocks(NvApiNative api, nint handle, string id, List<RawSensorReading> list)
    {
        if (!api.TryGetCurrentClocks(handle, out var clocks)) return;
        (int Domain, string Name)[] domains =
        [
            (NvClockFrequencies.Graphics, "GPU Core"),
            (NvClockFrequencies.Memory, "GPU Memory"),
            (NvClockFrequencies.Processor, "GPU Shader"),
            (NvClockFrequencies.Video, "GPU Video"),
        ];
        foreach (var (domain, name) in domains)
        {
            if (clocks.IsPresent(domain))
            {
                // Driver reports kHz; convert exactly to MHz and label the unit accordingly.
                list.Add(Reading(id, $"clock/{domain}", name, SensorType.Clock, SensorUnit.Megahertz, clocks.FrequencyKhz(domain) / 1000.0, domain));
            }
        }
    }

    private static RawSensorReading Reading(string deviceId, string suffix, string name, SensorType type, SensorUnit unit, double value, int index = 0, SensorKind? kind = null) =>
        new($"{deviceId}/{suffix}", deviceId, name, type, unit, value, ProviderName, SensorAvailability.Available, index, kind);

    public IReadOnlyList<RawSensorReading> ReadSensors()
    {
        lock (_gate) return _readings;
    }

    public IReadOnlyList<HardwareDevice> GetDevices() => _devices;

    public void Dispose()
    {
        lock (_gate)
        {
            _api?.Dispose();
            _api = null;
            _nvml?.Dispose();
            _nvml = null;
        }
    }
}
