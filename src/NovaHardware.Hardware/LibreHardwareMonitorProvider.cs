using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Identity;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using Lhm = LibreHardwareMonitor.Hardware;

namespace NovaHardware.Hardware;

/// <summary>
/// Primary sensor source backed by LibreHardwareMonitorLib (CPU, GPU, motherboard / Super I/O, memory,
/// storage, network, PSU, battery, coolers). Many low-level sensors require administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LibreHardwareMonitorProvider : IHardwareProvider
{
    public const string ProviderName = "LibreHardwareMonitor";

    private readonly ILogger<LibreHardwareMonitorProvider> _logger;
    private readonly Lock _gate = new();
    private Lhm.Computer? _computer;
    private List<HardwareDevice> _devices = [];
    private List<RawSensorReading> _readings = [];

    public LibreHardwareMonitorProvider(ILogger<LibreHardwareMonitorProvider> logger)
    {
        _logger = logger;
    }

    public string Name => ProviderName;
    public int Priority => 0;
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        try
        {
            var computer = new Lhm.Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                IsPsuEnabled = true,
                IsBatteryEnabled = true,
            };
            computer.Open();
            lock (_gate)
            {
                _computer = computer;
            }

            var admin = Elevation.IsAdministrator();
            Status = admin
                ? ProviderStatus.Ready($"{computer.Hardware.Count} hardware nodes")
                : new ProviderStatus(true, "Running without administrator rights — motherboard, voltage and some CPU sensors are unavailable.", RequiresElevation: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LibreHardwareMonitor failed to open");
            Status = ProviderStatus.Unavailable($"Failed to open: {ex.Message}");
        }
    }, cancellationToken);

    public void Update()
    {
        lock (_gate)
        {
            if (_computer is null)
            {
                return;
            }

            var devices = new List<HardwareDevice>();
            var readings = new List<RawSensorReading>();
            foreach (var hw in _computer.Hardware)
            {
                Visit(hw, null, devices, readings);
            }

            _devices = devices;
            _readings = readings;
        }
    }

    private void Visit(Lhm.IHardware hw, string? parentId, List<HardwareDevice> devices, List<RawSensorReading> readings)
    {
        try
        {
            hw.Update();
        }
        catch (Exception ex)
        {
            // A single misbehaving device (e.g. a USB cooler) must not take down the whole provider.
            _logger.LogDebug(ex, "Update failed for {Hardware}", hw.Name);
        }

        var deviceId = $"lhm:{hw.Identifier}";
        var category = LibreHardwareMonitorMapping.Map(hw.HardwareType, hw.Name);
        // Sub-hardware (e.g. Super I/O under a motherboard) is shown as its own device.
        devices.Add(new HardwareDevice(deviceId, hw.Name, category, ProviderName, parentId, LibreHardwareMonitorMapping.Vendor(hw.HardwareType), Identity(hw)));

        foreach (var sensor in hw.Sensors)
        {
            var (type, unit) = LibreHardwareMonitorMapping.Map(sensor.SensorType);
            double? value = sensor.Value is { } v && float.IsFinite(v) ? v : null;
            readings.Add(new RawSensorReading(
                SensorId: $"lhm:{sensor.Identifier}",
                DeviceId: deviceId,
                Name: sensor.Name,
                Type: type,
                Unit: unit,
                Value: value,
                ProviderName: ProviderName,
                Availability: value is null ? SensorAvailability.NoData : SensorAvailability.Available,
                Index: sensor.Index));
        }

        foreach (var sub in hw.SubHardware)
        {
            Visit(sub, deviceId, devices, readings);
        }
    }

    /// <summary>Physical identity: GPUs expose their PnP device path; "Total Memory" is the system-memory singleton.</summary>
    private static DeviceIdentity? Identity(Lhm.IHardware hw)
    {
        if (hw is Lhm.Gpu.GenericGpu gpu && !string.IsNullOrEmpty(gpu.DeviceId))
        {
            var id = PnpPath.FromPath(gpu.DeviceId);
            return id.IsEmpty ? null : id;
        }

        if (hw.HardwareType == Lhm.HardwareType.Memory && hw.Name.Equals("Total Memory", StringComparison.OrdinalIgnoreCase))
        {
            return new DeviceIdentity { SingletonKey = WindowsMemoryProvider.SystemMemoryKey };
        }

        return null;
    }

    public IReadOnlyList<RawSensorReading> ReadSensors()
    {
        lock (_gate) return _readings;
    }

    public IReadOnlyList<HardwareDevice> GetDevices()
    {
        lock (_gate) return _devices;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LibreHardwareMonitor close failed");
            }

            _computer = null;
        }
    }
}
