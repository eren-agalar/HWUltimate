using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using HWUltimate.Core.Abstractions;
using HWUltimate.Core.Detection;
using HWUltimate.Core.Identity;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;
using Lhm = LibreHardwareMonitor.Hardware;

namespace HWUltimate.Hardware;

/// <summary>
/// Primary sensor source backed by LibreHardwareMonitorLib (CPU, GPU, motherboard / Super I/O, memory,
/// storage, network, PSU, battery, coolers, power monitors). Many low-level sensors require administrator rights
/// and the PawnIO driver.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LibreHardwareMonitorProvider : IHardwareProvider, IRescannableProvider
{
    public const string ProviderName = "LibreHardwareMonitor";

    /// <summary>Official PawnIO site (driver LibreHardwareMonitor 0.9.5+ uses for Super I/O, SMBus, MSR and SMU access).</summary>
    public const string PawnIoUrl = "https://pawnio.eu/";

    private static readonly TimeSpan NetworkFactsLifetime = TimeSpan.FromSeconds(10);

    private readonly ILogger<LibreHardwareMonitorProvider> _logger;
    private readonly Lock _gate = new();
    private Lhm.Computer? _computer;
    private List<HardwareDevice> _devices = [];
    private List<RawSensorReading> _readings = [];
    private DetectionRules.DetectionSummary? _summary;
    private Dictionary<string, NetworkAdapterFacts> _networkFacts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _networkFactsAt = DateTime.MinValue;

    public LibreHardwareMonitorProvider(ILogger<LibreHardwareMonitorProvider> logger)
    {
        _logger = logger;
    }

    public string Name => ProviderName;
    public int Priority => 0;
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;

    /// <summary>True when the PawnIO driver is installed (null until the first open).</summary>
    public bool? LowLevelDriverInstalled { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_gate) OpenLocked();
    }, cancellationToken);

    /// <summary>
    /// Re-detects hardware when the current detection failed or looks incomplete (or always, when forced by the user).
    /// LibreHardwareMonitor's driver session is process-wide, so the old session is closed before a new one opens; polling
    /// waits on the provider lock for the few seconds this takes.
    /// </summary>
    public Task<RescanResult> RescanAsync(bool force, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_gate)
        {
            var before = _summary;
            var needed = force || _computer is null || !Status.IsAvailable || before is null || DetectionRules.LooksIncomplete(before);
            if (!needed) return RescanResult.Unchanged("detection complete");

            OpenLocked();
            if (before is not null && _summary is not null && DetectionRules.IsBetter(before, _summary))
            {
                // The new pass found less (bus busy again?). One more attempt, then keep whatever it finds.
                _logger.LogInformation("Re-scan found less than before ({After} < {Before} nodes); retrying once", _summary.HardwareNodes, before.HardwareNodes);
                OpenLocked();
            }

            var after = _summary;
            var changed = before is null || (after is not null && DetectionRules.IsBetter(after, before));
            return new RescanResult(changed, after is null ? Status.Message : $"{after.HardwareNodes} hardware nodes, {after.Sensors} sensors (was {before?.HardwareNodes ?? 0} / {before?.Sensors ?? 0})");
        }
    }, cancellationToken);

    /// <summary>Closes any open session and opens a fresh one. Caller holds <see cref="_gate"/>.</summary>
    private void OpenLocked()
    {
        CloseLocked();
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
                IsPowerMonitorEnabled = true,
            };
            computer.Open();
            _computer = computer;

            bool? driver;
            try { driver = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
            catch (Exception ex) { _logger.LogDebug(ex, "PawnIO check failed"); driver = null; }
            LowLevelDriverInstalled = driver;

            // Two passes: LibreHardwareMonitor activates some sensors (fans, GPU sensors) only after they first report a value.
            UpdateLocked();
            UpdateLocked();
            _summary = Summarize(computer, driver != false);
            LogDetection(computer, _summary);

            var admin = _summary.IsElevated;
            Status = (admin, driver) switch
            {
                (false, _) => new ProviderStatus(true, "Running without administrator rights — motherboard, voltage and some CPU sensors are unavailable.", RequiresElevation: true),
                (true, false) => new ProviderStatus(true, "The PawnIO driver is not installed — motherboard, fan, voltage and some CPU sensors are unavailable.", LowLevelDriverMissing: true),
                _ => ProviderStatus.Ready($"{_summary.HardwareNodes} hardware nodes, {_summary.Sensors} sensors"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LibreHardwareMonitor failed to open");
            CloseLocked();
            _summary = null;
            Status = ProviderStatus.Unavailable($"Failed to open: {ex.Message}");
        }
    }

    private static DetectionRules.DetectionSummary Summarize(Lhm.Computer computer, bool driverAvailable)
    {
        var all = Flatten(computer.Hardware).ToList();
        var board = all.FirstOrDefault(h => h.HardwareType == Lhm.HardwareType.Motherboard);
        var cpus = all.Where(h => h.HardwareType == Lhm.HardwareType.Cpu).ToList();
        return new DetectionRules.DetectionSummary(
            HardwareNodes: all.Count,
            Sensors: all.Sum(h => h.Sensors.Length),
            IsElevated: Elevation.IsAdministrator(),
            LowLevelDriverAvailable: driverAvailable,
            HasMotherboard: board is not null,
            HasMotherboardChip: board?.SubHardware.Length > 0,
            HasCpu: cpus.Count > 0,
            CpuHasTemperature: cpus.Any(c => c.Sensors.Any(s => s.SensorType == Lhm.SensorType.Temperature)));
    }

    private static IEnumerable<Lhm.IHardware> Flatten(IEnumerable<Lhm.IHardware> hardware)
    {
        foreach (var hw in hardware)
        {
            yield return hw;
            foreach (var sub in Flatten(hw.SubHardware)) yield return sub;
        }
    }

    private void LogDetection(Lhm.Computer computer, DetectionRules.DetectionSummary summary)
    {
        _logger.LogInformation("LibreHardwareMonitor detection: {Nodes} nodes, {Sensors} sensors, admin {Admin}, PawnIO {Driver}{Incomplete}",
            summary.HardwareNodes, summary.Sensors, summary.IsElevated, LowLevelDriverInstalled?.ToString() ?? "unknown",
            DetectionRules.LooksIncomplete(summary) ? " (looks incomplete; will re-scan)" : "");
        foreach (var hw in Flatten(computer.Hardware))
        {
            _logger.LogInformation("  {Type} \"{Name}\" {Id}: {Count} sensors", hw.HardwareType, hw.Name, hw.Identifier, hw.Sensors.Length);
        }
    }

    public void Update()
    {
        lock (_gate) UpdateLocked();
    }

    private void UpdateLocked()
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
        devices.Add(new HardwareDevice(deviceId, hw.Name, category, ProviderName, parentId, LibreHardwareMonitorMapping.Vendor(hw.HardwareType), Identity(hw))
        {
            Relevance = hw.HardwareType == Lhm.HardwareType.Network ? NetworkRelevance(hw) : DeviceRelevance.Primary,
        });

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

    // ------------------------------------------------------------------ network relevance
    private DeviceRelevance NetworkRelevance(Lhm.IHardware hw)
    {
        // LibreHardwareMonitor's network identifier is "/nic/{interface GUID}".
        var id = hw.Identifier.ToString();
        var guid = id[(id.LastIndexOf('/') + 1)..];
        if (DateTime.UtcNow - _networkFactsAt > NetworkFactsLifetime)
        {
            _networkFacts = ReadNetworkFacts();
            _networkFactsAt = DateTime.UtcNow;
        }

        return DetectionRules.ClassifyNetworkAdapter(_networkFacts.TryGetValue(guid, out var facts) ? facts : new NetworkAdapterFacts(hw.Name));
    }

    private Dictionary<string, NetworkAdapterFacts> ReadNetworkFacts()
    {
        var result = new Dictionary<string, NetworkAdapterFacts>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var characteristics = ReadAdapterCharacteristics();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                // NCF_VIRTUAL 0x1, NCF_PHYSICAL 0x4, NCF_HIDDEN 0x8 (Windows network driver characteristics).
                uint? flags = characteristics.TryGetValue(ni.Id, out var c) ? c : null;
                result[ni.Id] = new NetworkAdapterFacts(
                    ni.Name,
                    ni.Description,
                    IsUp: ni.OperationalStatus == OperationalStatus.Up,
                    IsLoopbackOrTunnel: ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel,
                    IsPhysical: flags is { } f1 ? (f1 & 0x4) != 0 : null,
                    IsVirtualOrHidden: flags is { } f2 ? (f2 & 0x9) != 0 : null);
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Network adapter facts unavailable; all adapters shown");
        }

        return result;
    }

    private static Dictionary<string, uint> ReadAdapterCharacteristics()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return map;
        foreach (var name in cls.GetSubKeyNames())
        {
            using var key = cls.OpenSubKey(name);
            if (key?.GetValue("NetCfgInstanceId") is string id && key.GetValue("Characteristics") is int c) map[id] = unchecked((uint)c);
        }

        return map;
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

    /// <summary>LibreHardwareMonitor's own text report (hardware tree, sensors, chip registers) for diagnostics.</summary>
    public string? GetDiagnosticReport()
    {
        lock (_gate)
        {
            try { return _computer?.GetReport(); }
            catch (Exception ex) { _logger.LogWarning(ex, "LibreHardwareMonitor report failed"); return null; }
        }
    }

    public IReadOnlyList<RawSensorReading> ReadSensors()
    {
        lock (_gate) return _readings;
    }

    public IReadOnlyList<HardwareDevice> GetDevices()
    {
        lock (_gate) return _devices;
    }

    private void CloseLocked()
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
        _devices = [];
        _readings = [];
    }

    public void Dispose()
    {
        lock (_gate) CloseLocked();
    }
}
