using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Hardware.Nvidia;

/// <summary>NVIDIA-specific telemetry directly from NVAPI: temperatures by target, engine utilisation, clocks and fan tach.</summary>
[SupportedOSPlatform("windows")]
public sealed class NvApiProvider : IHardwareProvider
{
    public const string ProviderName = "NVAPI";

    private static readonly string[] UtilizationDomains = ["GPU Core", "Memory Controller", "Video Engine", "Bus Interface"];

    private readonly ILogger<NvApiProvider> _logger;
    private readonly Lock _gate = new();
    private NvApiNative? _api;
    private (nint Handle, string Id, string Name)[] _gpus = [];
    private List<HardwareDevice> _devices = [];
    private List<RawSensorReading> _readings = [];

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
            var api = NvApiNative.TryLoad(out var reason);
            if (api is null)
            {
                Status = ProviderStatus.Unavailable(reason);
                return Task.CompletedTask;
            }

            var handles = api.EnumPhysicalGpus();
            _gpus = handles.Select((h, i) => (h, $"nvapi:gpu{i}", api.GetFullName(h) ?? $"NVIDIA GPU #{i}")).ToArray();
            DriverVersion = api.GetDriverVersion();
            _devices = _gpus.Select(g => new HardwareDevice(g.Id, g.Name, HardwareCategory.Gpu, ProviderName, Vendor: "NVIDIA")).ToList();
            _api = api;
            Status = _gpus.Length == 0
                ? ProviderStatus.Unavailable("NVAPI loaded but no physical NVIDIA GPUs were enumerated.")
                : ProviderStatus.Ready($"{_gpus.Length} GPU(s), driver {DriverVersion ?? "unknown"}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NVAPI initialisation failed");
            Status = ProviderStatus.Unavailable($"NVAPI error: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void Update()
    {
        lock (_gate)
        {
            if (_api is null)
            {
                return;
            }

            var list = new List<RawSensorReading>();
            foreach (var (handle, id, _) in _gpus)
            {
                ReadThermals(_api, handle, id, list);
                ReadUtilization(_api, handle, id, list);
                ReadClocks(_api, handle, id, list);

                if (_api.GetTachRpm(handle) is { } rpm)
                {
                    list.Add(Reading(id, "fan/0", "GPU Fan", SensorType.FanSpeed, SensorUnit.Rpm, rpm));
                }
            }

            _readings = list;
        }
    }

    private static void ReadThermals(NvApiNative api, nint handle, string id, List<RawSensorReading> list)
    {
        if (!api.TryGetThermal(handle, out var thermal))
        {
            return;
        }

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
        if (!api.TryGetUtilization(handle, out var info))
        {
            return;
        }

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
        if (!api.TryGetCurrentClocks(handle, out var clocks))
        {
            return;
        }

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

    private static RawSensorReading Reading(string deviceId, string suffix, string name, SensorType type, SensorUnit unit, double value, int index = 0) =>
        new($"{deviceId}/{suffix}", deviceId, name, type, unit, value, ProviderName, SensorAvailability.Available, index);

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
        }
    }
}
