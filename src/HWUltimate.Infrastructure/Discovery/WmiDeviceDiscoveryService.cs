using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Hardware;
using HWUltimate.Infrastructure.Smbios;

namespace HWUltimate.Infrastructure.Discovery;

/// <summary>Hardware identification through WMI / CIM (root\cimv2, root\Microsoft\Windows\Storage, root\wmi).</summary>
[SupportedOSPlatform("windows")]
public sealed class WmiDeviceDiscoveryService : IDeviceDiscoveryService, ISystemSummaryProvider
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly ILogger<WmiDeviceDiscoveryService> _logger;

    public WmiDeviceDiscoveryService(ILogger<WmiDeviceDiscoveryService> logger)
    {
        _logger = logger;
    }

    public string SourceName => "WMI";

    private List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(15);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogDebug(ex, "WMI query failed: {Query} in {Scope}", wql, scope);
            return [];
        }
    }

    private static string? S(ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            var s = v?.ToString()?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static T? N<T>(ManagementBaseObject o, string prop) where T : struct
    {
        try
        {
            return o[prop] is { } v ? (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string Gb(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:0.##} GB";

    public Task<SystemSummary> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var os = Query("SELECT Caption, Version, BuildNumber, LastBootUpTime, OSArchitecture FROM Win32_OperatingSystem").FirstOrDefault();
        var cs = Query("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        var board = Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        var cpus = Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        var gpus = Query("SELECT Name FROM Win32_VideoController").Select(g => S(g, "Name")).OfType<string>().ToList();
        var mem = Query("SELECT Capacity FROM Win32_PhysicalMemory").Select(m => N<ulong>(m, "Capacity") ?? 0).Aggregate(0UL, (a, b) => a + b);

        TimeSpan? uptime = null;
        if (os is not null && S(os, "LastBootUpTime") is { } boot)
        {
            try
            {
                uptime = DateTime.Now - ManagementDateTimeConverter.ToDateTime(boot);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        string? biosDate = null;
        if (bios is not null && S(bios, "ReleaseDate") is { } rd)
        {
            try
            {
                biosDate = ManagementDateTimeConverter.ToDateTime(rd).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return new SystemSummary
        {
            OperatingSystem = os is null ? null : $"{S(os, "Caption")} {S(os, "OSArchitecture")}".Trim(),
            OsVersion = os is null ? null : S(os, "Version"),
            OsBuild = os is null ? null : S(os, "BuildNumber"),
            SystemManufacturer = cs is null ? null : S(cs, "Manufacturer"),
            SystemModel = cs is null ? null : S(cs, "Model"),
            Motherboard = board is null ? null : $"{S(board, "Manufacturer")} {S(board, "Product")}".Trim(),
            BiosVersion = bios is null ? null : S(bios, "SMBIOSBIOSVersion"),
            BiosDate = biosDate,
            Processor = cpus.Count == 0 ? null : string.Join(" + ", cpus.Select(c => S(c, "Name")).OfType<string>().Distinct()),
            PhysicalCores = cpus.Count == 0 ? null : cpus.Sum(c => N<int>(c, "NumberOfCores") ?? 0),
            LogicalProcessors = cpus.Count == 0 ? Environment.ProcessorCount : cpus.Sum(c => N<int>(c, "NumberOfLogicalProcessors") ?? 0),
            TotalMemoryBytes = mem > 0 ? mem : cs is null ? null : N<ulong>(cs, "TotalPhysicalMemory"),
            Gpus = gpus,
            Uptime = uptime ?? TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }, cancellationToken);

    public Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DeviceInfo>>(() =>
    {
        var list = new List<DeviceInfo>();
        list.AddRange(Processors());
        // GPUs are described by GpuDiscoveryService (WMI + PCI + DXGI) so each adapter appears exactly once.
        list.AddRange(MemoryModules());
        list.AddRange(Disks());
        list.AddRange(NetworkAdapters());
        list.AddRange(Batteries());
        list.AddRange(Monitors());
        list.AddRange(Audio());
        return list;
    }, cancellationToken);

    private IEnumerable<DeviceInfo> Processors()
    {
        foreach (var (o, i) in Query("SELECT * FROM Win32_Processor").Select((o, i) => (o, i)))
        {
            yield return new DeviceInfo
            {
                Id = $"wmi:cpu{i}",
                Name = S(o, "Name") ?? "Processor",
                Category = HardwareCategory.Cpu,
                Manufacturer = S(o, "Manufacturer"),
                Model = S(o, "Name"),
                Source = SourceName,
                Properties = SmbiosDiscoveryService.Props(
                    ("Cores", S(o, "NumberOfCores")),
                    ("Logical processors", S(o, "NumberOfLogicalProcessors")),
                    ("Base clock", N<uint>(o, "MaxClockSpeed") is { } mhz ? $"{mhz} MHz" : null),
                    ("L2 cache", N<uint>(o, "L2CacheSize") is { } l2 and > 0 ? $"{l2} KB" : null),
                    ("L3 cache", N<uint>(o, "L3CacheSize") is { } l3 and > 0 ? $"{l3 / 1024.0:0.#} MB" : null),
                    ("Socket", S(o, "SocketDesignation")),
                    ("Processor ID", S(o, "ProcessorId")),
                    ("Description", S(o, "Description")),
                    ("Virtualization (firmware)", N<bool>(o, "VirtualizationFirmwareEnabled") is { } v ? (v ? "Enabled" : "Disabled") : null)),
            };
        }
    }

    private IEnumerable<DeviceInfo> VideoControllers()
    {
        foreach (var (o, i) in Query("SELECT * FROM Win32_VideoController").Select((o, i) => (o, i)))
        {
            var name = S(o, "Name") ?? "Display adapter";
            // Win32_VideoController.AdapterRAM is a uint32 and saturates at 4 GB; the driver registry key holds the real size.
            var vram = DedicatedVideoMemory(name) ?? N<uint>(o, "AdapterRAM");
            yield return new DeviceInfo
            {
                Id = $"wmi:gpu{i}",
                Name = name,
                Category = HardwareCategory.Gpu,
                Manufacturer = S(o, "AdapterCompatibility"),
                Model = S(o, "VideoProcessor"),
                DriverVersion = S(o, "DriverVersion"),
                Source = SourceName,
                Properties = SmbiosDiscoveryService.Props(
                    ("Dedicated memory", vram is { } bytes and > 0 ? Gb(bytes) : null),
                    ("Driver version", S(o, "DriverVersion")),
                    ("Driver date", S(o, "DriverDate") is { } dd ? SafeDate(dd) : null),
                    ("Current mode", N<uint>(o, "CurrentHorizontalResolution") is { } w and > 0
                        ? $"{w} × {S(o, "CurrentVerticalResolution")} @ {S(o, "CurrentRefreshRate")} Hz" : null),
                    ("PNP device ID", S(o, "PNPDeviceID"))),
            };
        }
    }

    private static ulong? DedicatedVideoMemory(string adapterName)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (root is null) return null;
            foreach (var sub in root.GetSubKeyNames().Where(n => n.Length == 4))
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is string desc && desc.Equals(adapterName, StringComparison.OrdinalIgnoreCase))
                {
                    return key.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => (ulong)l,
                        byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b),
                        int x => (uint)x,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b),
                        _ => null,
                    };
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return null;
    }

    private IEnumerable<DeviceInfo> MemoryModules()
    {
        foreach (var (o, i) in Query("SELECT * FROM Win32_PhysicalMemory").Select((o, i) => (o, i)))
        {
            var capacity = N<ulong>(o, "Capacity") ?? 0;
            var smbiosType = N<uint>(o, "SMBIOSMemoryType");
            yield return new DeviceInfo
            {
                Id = $"wmi:mem{i}",
                Name = SmbiosDiscoveryService.MemoryName(S(o, "Manufacturer"), S(o, "PartNumber"), S(o, "DeviceLocator")),
                Category = HardwareCategory.Memory,
                Manufacturer = S(o, "Manufacturer"),
                Model = S(o, "PartNumber"),
                SerialNumber = S(o, "SerialNumber"),
                Source = SourceName,
                Properties = SmbiosDiscoveryService.Props(
                    ("Capacity", capacity > 0 ? Gb(capacity) : null),
                    ("Type", smbiosType is { } t ? SmbiosParser.MemoryTypeName((byte)t) : null),
                    ("Slot", S(o, "DeviceLocator")),
                    ("Bank", S(o, "BankLabel")),
                    ("Rated speed", N<uint>(o, "Speed") is { } sp and > 0 ? $"{sp} MT/s" : null),
                    ("Configured speed", N<uint>(o, "ConfiguredClockSpeed") is { } cs and > 0 ? $"{cs} MT/s" : null),
                    ("Configured voltage", N<uint>(o, "ConfiguredVoltage") is { } mv and > 0 ? $"{mv / 1000.0:0.000} V" : null)),
            };
        }
    }

    private IEnumerable<DeviceInfo> Disks()
    {
        var physical = Query("SELECT * FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        if (physical.Count > 0)
        {
            foreach (var o in physical)
            {
                var size = N<ulong>(o, "Size") ?? 0;
                var reliability = ReliabilityCounters(S(o, "ObjectId"));
                yield return new DeviceInfo
                {
                    Id = $"wmi:disk{S(o, "DeviceId")}",
                    Name = S(o, "FriendlyName") ?? "Disk",
                    Category = HardwareCategory.Storage,
                    Model = S(o, "Model"),
                    SerialNumber = S(o, "SerialNumber"),
                    DriverVersion = S(o, "FirmwareVersion"),
                    Source = SourceName,
                    Properties = SmbiosDiscoveryService.Props(
                        ("Capacity", size > 0 ? $"{size / 1e9:0.#} GB ({Gb(size)})" : null),
                        ("Media type", (N<ushort>(o, "MediaType") ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null }),
                        ("Bus", (N<ushort>(o, "BusType") ?? 0) switch { 1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => null }),
                        ("Health", (N<ushort>(o, "HealthStatus") ?? 99) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null }),
                        ("Firmware", S(o, "FirmwareVersion")),
                        ("Spindle speed", N<uint>(o, "SpindleSpeed") is { } rpm and > 0 and < uint.MaxValue ? $"{rpm} RPM" : null),
                        ("Operational status", OperationalStatus(o)),
                        ("Logical sector size", N<ulong>(o, "LogicalSectorSize") is { } ls and > 0 ? $"{ls} bytes" : null),
                        ("Physical sector size", N<ulong>(o, "PhysicalSectorSize") is { } ps and > 0 ? $"{ps} bytes" : null))
                        .Concat(reliability).ToList(),
                };
            }

            yield break;
        }

        foreach (var (o, i) in Query("SELECT * FROM Win32_DiskDrive").Select((o, i) => (o, i)))
        {
            var size = N<ulong>(o, "Size") ?? 0;
            yield return new DeviceInfo
            {
                Id = $"wmi:disk{i}",
                Name = S(o, "Model") ?? "Disk",
                Category = HardwareCategory.Storage,
                Model = S(o, "Model"),
                SerialNumber = S(o, "SerialNumber"),
                DriverVersion = S(o, "FirmwareRevision"),
                Source = SourceName,
                Properties = SmbiosDiscoveryService.Props(
                    ("Capacity", size > 0 ? $"{size / 1e9:0.#} GB" : null),
                    ("Interface", S(o, "InterfaceType")),
                    ("Media", S(o, "MediaType")),
                    ("Partitions", S(o, "Partitions"))),
            };
        }
    }

    private static string? OperationalStatus(ManagementBaseObject o)
    {
        try
        {
            return o["OperationalStatus"] is ushort[] codes && codes.Length > 0
                ? string.Join(", ", codes.Select(c => c switch { 2 => "OK", 3 => "Degraded", 5 => "Predictive failure", 6 => "Error", 10 => "Stopped", 11 => "In service", 13 => "Lost communication", 0xD00D => "Transient error", _ => c.ToString(CultureInfo.InvariantCulture) }))
                : null;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    /// <summary>
    /// MSFT_StorageReliabilityCounter: the drive's own SMART / NVMe health log as normalised by Windows.
    /// Reading it requires administrator rights; unavailable counters are reported explicitly.
    /// </summary>
    private List<HardwareProperty> ReliabilityCounters(string? objectId)
    {
        const string src = "MSFT_StorageReliabilityCounter";
        var list = new List<HardwareProperty>();
        if (objectId is null) return list;
        var escaped = objectId.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var rc = Query($"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{escaped}\"}} WHERE ResultClass = MSFT_StorageReliabilityCounter", @"root\Microsoft\Windows\Storage").FirstOrDefault();
        if (rc is null)
        {
            list.Add(new HardwareProperty("SMART / health log", HardwareProperty.NotAvailableText, PropertySections.Advanced, src,
                IsAdmin() ? PropertyStatus.NotAvailable : PropertyStatus.RequiresElevation,
                "The drive or its driver does not expose reliability counters to Windows, or administrator rights are required."));
            return list;
        }

        void Add(string section, string name, string? value) { if (value is not null) list.Add(new HardwareProperty(name, value, section, src)); }
        Add(PropertySections.Overview, "Temperature", N<byte>(rc, "Temperature") is { } t and > 0 ? $"{t} °C" : null);
        Add(PropertySections.Performance, "Maximum temperature recorded", N<byte>(rc, "TemperatureMax") is { } tm and > 0 ? $"{tm} °C" : null);
        Add(PropertySections.Overview, "Wear (percentage used)", N<byte>(rc, "Wear") is { } w ? $"{w} %" : null);
        Add(PropertySections.Overview, "Power-on hours", N<uint>(rc, "PowerOnHours") is { } h ? $"{h:N0} h" : null);
        Add(PropertySections.Advanced, "Start / stop cycles", N<uint>(rc, "StartStopCycleCount")?.ToString("N0", CultureInfo.InvariantCulture));
        Add(PropertySections.Advanced, "Load / unload cycles", N<uint>(rc, "LoadUnloadCycleCount")?.ToString("N0", CultureInfo.InvariantCulture));
        Add(PropertySections.Advanced, "Read errors (total / corrected / uncorrected)", N<ulong>(rc, "ReadErrorsTotal") is { } re ? $"{re:N0} / {N<ulong>(rc, "ReadErrorsCorrected") ?? 0:N0} / {N<ulong>(rc, "ReadErrorsUncorrected") ?? 0:N0}" : null);
        Add(PropertySections.Advanced, "Write errors (total / corrected / uncorrected)", N<ulong>(rc, "WriteErrorsTotal") is { } we ? $"{we:N0} / {N<ulong>(rc, "WriteErrorsCorrected") ?? 0:N0} / {N<ulong>(rc, "WriteErrorsUncorrected") ?? 0:N0}" : null);
        Add(PropertySections.Performance, "Max read latency", N<ulong>(rc, "ReadLatencyMax") is { } rl and > 0 ? $"{rl:N0} ms" : null);
        Add(PropertySections.Performance, "Max write latency", N<ulong>(rc, "WriteLatencyMax") is { } wl and > 0 ? $"{wl:N0} ms" : null);
        Add(PropertySections.Advanced, "Manufacture date", S(rc, "ManufactureDate"));
        list.Add(HardwareProperty.Unavailable("Total host reads / writes", PropertySections.Overview,
            "Not part of the Windows reliability counter. When the sensor provider reads it from SMART / NVMe logs it appears under Live sensors (Data Read / Data Written).", src));
        return list;
    }

    private IEnumerable<DeviceInfo> NetworkAdapters()
    {
        foreach (var (o, i) in Query("SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE").Select((o, i) => (o, i)))
        {
            yield return new DeviceInfo
            {
                Id = $"wmi:net{i}",
                Name = S(o, "Name") ?? "Network adapter",
                Category = HardwareCategory.Network,
                Manufacturer = S(o, "Manufacturer"),
                Source = SourceName,
                Properties = SmbiosDiscoveryService.Props(
                    ("Connection", S(o, "NetConnectionID")),
                    ("MAC address", S(o, "MACAddress")),
                    ("Link speed", N<ulong>(o, "Speed") is { } bps and > 0 and < ulong.MaxValue ? $"{bps / 1e6:0} Mbps" : null),
                    ("Enabled", N<bool>(o, "NetEnabled") is { } e ? (e ? "Yes" : "No") : null))
                    .Concat(NetworkConnection.Describe(S(o, "Name"), S(o, "MACAddress"))).ToList(),
            };
        }
    }

    private IEnumerable<DeviceInfo> Batteries()
    {
        foreach (var (o, i) in Query("SELECT * FROM Win32_Battery").Select((o, i) => (o, i)))
        {
            yield return new DeviceInfo
            {
                Id = $"wmi:battery{i}",
                Name = S(o, "Name") ?? "Battery",
                Category = HardwareCategory.Battery,
                Source = SourceName,
                Properties = SmbiosDiscoveryService.Props(
                    ("Charge", S(o, "EstimatedChargeRemaining") is { } c ? $"{c} %" : null),
                    ("Chemistry", (N<ushort>(o, "Chemistry") ?? 0) switch { 3 => "Lead Acid", 4 => "Nickel Cadmium", 5 => "Nickel Metal Hydride", 6 => "Lithium-ion", 8 => "Lithium Polymer", _ => null }),
                    ("Device ID", S(o, "DeviceID"))),
            };
        }
    }

    private IEnumerable<DeviceInfo> Monitors()
    {
        var connections = Query("SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", @"root\wmi")
            .Where(c => S(c, "InstanceName") is not null)
            .ToDictionary(c => S(c, "InstanceName")!, c => N<uint>(c, "VideoOutputTechnology"), StringComparer.OrdinalIgnoreCase);
        foreach (var (o, i) in Query("SELECT * FROM WmiMonitorID", @"root\wmi").Select((o, i) => (o, i)))
        {
            var name = Decode(o, "UserFriendlyName") ?? "Monitor";
            yield return new DeviceInfo
            {
                Id = $"wmi:monitor{i}",
                Name = name,
                Category = HardwareCategory.Display,
                Manufacturer = Decode(o, "ManufacturerName"),
                SerialNumber = Decode(o, "SerialNumberID"),
                Source = SourceName,
                Properties = SmbiosDiscoveryService.Props(
                    ("Manufacturer code", Decode(o, "ManufacturerName")),
                    ("Product code", Decode(o, "ProductCodeID")),
                    ("Manufactured", N<ushort>(o, "YearOfManufacture") is { } y and > 0 ? $"Week {S(o, "WeekOfManufacture")}, {y}" : null),
                    ("Connection", S(o, "InstanceName") is { } inst && connections.TryGetValue(inst, out var tech) ? OutputTechnology(tech) : null)),
            };
        }

        static string? Decode(ManagementBaseObject o, string prop)
        {
            try
            {
                return o[prop] is ushort[] chars
                    ? new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() is { Length: > 0 } s ? s : null
                    : null;
            }
            catch (ManagementException)
            {
                return null;
            }
        }
    }

    internal static string? OutputTechnology(uint? code) => code switch
    {
        null => null,
        0 => "VGA (HD15)",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS (internal)",
        8 => "D-Jpn",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "DisplayPort (embedded / eDP)",
        12 => "UDI",
        13 => "UDI (embedded)",
        14 => "SDTV dongle",
        15 => "Miracast (wireless)",
        16 => "Indirect wired",
        0x80000000 => "Internal",
        _ => $"Other ({code})",
    };

    private IEnumerable<DeviceInfo> Audio()
    {
        foreach (var (o, i) in Query("SELECT * FROM Win32_SoundDevice").Select((o, i) => (o, i)))
        {
            yield return new DeviceInfo
            {
                Id = $"wmi:audio{i}",
                Name = S(o, "Name") ?? "Audio device",
                Category = HardwareCategory.Audio,
                Manufacturer = S(o, "Manufacturer"),
                Source = SourceName,
                Properties = SmbiosDiscoveryService.Props(("Status", S(o, "Status")), ("PNP device ID", S(o, "PNPDeviceID"))),
            };
        }
    }

    private static string? SafeDate(string wmiDate)
    {
        try
        {
            return ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
