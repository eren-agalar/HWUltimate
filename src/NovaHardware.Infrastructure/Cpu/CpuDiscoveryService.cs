using System.Globalization;
using System.Management;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.HardwareInfo;
using NovaHardware.Domain.Hardware;
using S = NovaHardware.Domain.Hardware.PropertySections;

namespace NovaHardware.Infrastructure.Cpu;

/// <summary>
/// Detailed processor identification: CPUID (vendor, brand, signature, features, caches, virtualisation),
/// Windows topology (cores, P/E split, cache instances), the registry (microcode revision) and WMI (socket, firmware VT state).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CpuDiscoveryService : IDeviceDiscoveryService
{
    private const string CpuRegistryKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private readonly ILogger<CpuDiscoveryService> _logger;

    public CpuDiscoveryService(ILogger<CpuDiscoveryService> logger)
    {
        _logger = logger;
    }

    public string SourceName => "CPUID";

    public static CpuIdRegisters NativeCpuId(uint leaf, uint subleaf)
    {
        var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), unchecked((int)subleaf));
        return new CpuIdRegisters((uint)a, (uint)b, (uint)c, (uint)d);
    }

    public Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DeviceInfo>>(() =>
    {
        if (!X86Base.IsSupported)
        {
            return [];
        }

        try
        {
            var id = CpuIdDecoder.Decode(NativeCpuId);
            ProcessorTopology? topo = null;
            try { topo = ProcessorTopology.Query(); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            var (microcode, nominalMhz) = ReadRegistry();
            var wmi = ReadWmi();
            return [Build(id, topo, microcode, nominalMhz, wmi)];
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ManagementException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "CPU discovery failed");
            return [];
        }
    }, cancellationToken);

    internal sealed record WmiCpu(string? Name, string? Socket, int? MaxClockMhz, bool? VtFirmware, bool? Slat, string? Manufacturer);

    private static (string? Microcode, int? NominalMhz) ReadRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CpuRegistryKey);
            if (key is null) return (null, null);
            string? mc = null;
            if (key.GetValue("Update Revision") is byte[] rev && rev.Length >= 8)
            {
                // Intel stores the revision in the high DWORD (IA32_BIOS_SIGN_ID[63:32]); AMD in the low DWORD (MSR PATCH_LEVEL).
                var high = BitConverter.ToUInt32(rev, 4);
                var low = BitConverter.ToUInt32(rev, 0);
                var v = high != 0 ? high : low;
                if (v != 0) mc = $"0x{v:X}";
            }

            var mhz = key.GetValue("~MHz") is int m && m > 0 ? m : (int?)null;
            return (mc, mhz);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }

    private static WmiCpu? ReadWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, SocketDesignation, MaxClockSpeed, VirtualizationFirmwareEnabled, SecondLevelAddressTranslationExtensions, Manufacturer FROM Win32_Processor");
            var o = searcher.Get().Cast<ManagementBaseObject>().FirstOrDefault();
            if (o is null) return null;
            return new WmiCpu(o["Name"]?.ToString()?.Trim(), o["SocketDesignation"]?.ToString()?.Trim(),
                o["MaxClockSpeed"] is uint mc ? (int)mc : null,
                o["VirtualizationFirmwareEnabled"] as bool?, o["SecondLevelAddressTranslationExtensions"] as bool?, o["Manufacturer"]?.ToString());
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    internal static DeviceInfo Build(CpuIdInfo id, ProcessorTopology? topo, string? microcode, int? nominalMhz, WmiCpu? wmi)
    {
        var props = new List<HardwareProperty>();
        void Add(string section, string name, string? value, string source)
        {
            if (!string.IsNullOrWhiteSpace(value)) props.Add(new HardwareProperty(name, value, section, source));
        }

        void Na(string section, string name, string note, string? sources = null) => props.Add(HardwareProperty.Unavailable(name, section, note, sources));

        var vendorName = id.Vendor switch { "GenuineIntel" => "Intel", "AuthenticAMD" => "AMD", "HygonGenuine" => "Hygon", _ => id.Vendor };

        // Overview
        Add(S.Overview, "Processor", id.Brand ?? wmi?.Name, "CPUID 0x80000002-4");
        Add(S.Overview, "Manufacturer", vendorName, "CPUID 0");
        if (topo is not null)
        {
            Add(S.Overview, "Cores / threads", $"{topo.Cores} / {topo.LogicalProcessors}", "GetLogicalProcessorInformationEx");
        }

        // Identification
        Add(S.Identification, "Vendor string", id.Vendor, "CPUID 0");
        Add(S.Identification, "Signature", $"0x{id.Signature:X8}", "CPUID 1");
        Add(S.Identification, "Family / model / stepping", $"0x{id.Family:X} / 0x{id.Model:X} / 0x{id.Stepping:X}", "CPUID 1 (display values)");
        if (id.Microarchitecture is null) Na(S.Identification, "Microarchitecture", "Family/model combination is not in Nova's mapping table.", "CPUID");
        else Add(S.Identification, "Microarchitecture", id.Microarchitecture, "CPUID family/model mapping");
        if (id.Codename is null) Na(S.Identification, "Codename", "Family/model combination is not in Nova's mapping table.", "CPUID");
        else Add(S.Identification, "Codename", id.Codename, "CPUID family/model mapping");
        Add(S.Identification, "Socket", wmi?.Socket, "WMI Win32_Processor / SMBIOS");
        Add(S.Identification, "Max CPUID leaf", $"0x{id.MaxLeaf:X} / 0x{id.MaxExtendedLeaf:X8}", "CPUID");

        // Specifications
        if (topo is not null)
        {
            Add(S.Specifications, "Packages", topo.Packages.ToString(CultureInfo.InvariantCulture), "GetLogicalProcessorInformationEx");
            Add(S.Specifications, "Cores", topo.Cores.ToString(CultureInfo.InvariantCulture), "GetLogicalProcessorInformationEx");
            Add(S.Specifications, "Threads", topo.LogicalProcessors.ToString(CultureInfo.InvariantCulture), "GetLogicalProcessorInformationEx");
            Add(S.Specifications, "Simultaneous multithreading", topo.Smt ? "Enabled" : "Not active", "GetLogicalProcessorInformationEx");
            if (topo.HybridSplit is { } split)
            {
                Add(S.Specifications, "Performance cores (P)", split.PCores.ToString(CultureInfo.InvariantCulture), "Windows efficiency class");
                Add(S.Specifications, "Efficiency cores (E)", split.ECores.ToString(CultureInfo.InvariantCulture), "Windows efficiency class");
            }
            else
            {
                Add(S.Specifications, "Hybrid architecture", id.IsHybrid ? "Yes (CPUID) — single efficiency class reported" : "No", "CPUID 7 EDX[15]");
            }

            foreach (var c in topo.Caches.Where(c => c.Type != "Trace"))
            {
                var label = c.Type switch { "Instruction" => $"L{c.Level} instruction cache", "Data" => $"L{c.Level} data cache", _ => $"L{c.Level} cache" };
                Add(S.Specifications, label, $"{Size(c.SizeBytes)} × {c.Instances} ({c.Associativity}-way, {c.LineSize}-byte line)", "GetLogicalProcessorInformationEx");
            }
        }
        else
        {
            foreach (var c in id.Caches)
            {
                Add(S.Specifications, $"{c.Label} cache", $"{Size(c.SizeBytes)} per instance ({c.Ways}-way, {c.LineSize}-byte line)", "CPUID cache leaf");
            }
        }

        var baseMhz = id.BaseFrequencyMhz ?? wmi?.MaxClockMhz;
        Add(S.Specifications, "Base frequency", baseMhz is { } b ? $"{b} MHz" : null, id.BaseFrequencyMhz is not null ? "CPUID 0x16" : "WMI MaxClockSpeed");
        if (id.MaxFrequencyMhz is { } max) Add(S.Specifications, "Maximum frequency", $"{max} MHz", "CPUID 0x16");
        else Na(S.Specifications, "Maximum boost frequency", "Not reported by CPUID on this processor (leaf 0x16 is Intel-only). Observe the live clock sensors instead.", "CPUID 0x16");
        if (id.BusFrequencyMhz is { } bus) Add(S.Specifications, "Bus / reference clock", $"{bus} MHz", "CPUID 0x16");
        Na(S.Specifications, "TDP", "Processor TDP is not exposed by CPUID, SMBIOS or WMI. See the manufacturer's specification; live package power is shown in the Sensors page.", "CPUID, SMBIOS, WMI");

        // Performance capabilities
        Add(S.Performance, "Instruction sets", string.Join(", ", id.Features), "CPUID 1, 7, 0x80000001");
        Add(S.Performance, "64-bit (x86-64)", id.Is64Bit ? "Supported" : "Not supported", "CPUID 0x80000001");
        Add(S.Performance, "AVX-512", id.Features.Any(f => f.StartsWith("AVX-512", StringComparison.Ordinal)) ? "Supported" : "Not supported", "CPUID 7");
        Add(S.Performance, "Hardware virtualization", id.HardwareVirtualization ? id.VirtualizationTechnology! : id.HypervisorPresent ? "Hidden by active hypervisor" : "Not supported", "CPUID");
        if (wmi?.VtFirmware is { } vt) Add(S.Performance, "Virtualization enabled in firmware", vt ? "Yes" : "No", "WMI Win32_Processor");
        if (wmi?.Slat is { } slat) Add(S.Performance, "Second-level address translation", slat ? "Supported (EPT / NPT)" : "Not supported", "WMI Win32_Processor");
        Add(S.Performance, "Hypervisor present", id.HypervisorPresent ? $"Yes{(id.HypervisorVendor is { } hv ? $" ({hv})" : "")}" : "No", "CPUID 1 ECX[31]");

        // Firmware
        if (microcode is not null) Add(S.Firmware, "Microcode revision", microcode, "Windows registry (Update Revision)");
        else Na(S.Firmware, "Microcode revision", "Windows did not record a microcode revision for this processor.", "Registry");
        if (nominalMhz is { } nm) Add(S.Firmware, "Nominal clock (Windows)", $"{nm} MHz", "Windows registry (~MHz)");

        // Advanced
        foreach (var c in id.Caches)
        {
            Add(S.Advanced, $"CPUID {c.Label}", $"{Size(c.SizeBytes)}, {c.Ways}-way, {c.LineSize} B line, shared by ≤{c.MaxThreadsSharing} threads", "CPUID cache leaf");
        }

        return new DeviceInfo
        {
            Id = "cpuid:cpu0",
            Name = wmi?.Name ?? id.Brand ?? "Processor",
            Category = HardwareCategory.Cpu,
            Manufacturer = vendorName,
            Model = id.Brand,
            Source = "CPUID",
            Properties = props,
        };
    }

    private static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1024d / 1024:0.#} MB" : $"{bytes / 1024} KB";
}
