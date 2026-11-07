using System.Runtime.InteropServices;
using System.Text;

namespace HWUltimate.Hardware.Nvidia;

/// <summary>
/// Binding to the NVIDIA Management Library (nvml.dll, installed with the driver). Only documented, stable
/// entry points are used and each one is optional: if an export is missing or returns an error, the value is
/// reported as unavailable.
/// </summary>
internal sealed unsafe class NvmlNative : IDisposable
{
    public const int Success = 0;
    private readonly nint _lib;
    private readonly Dictionary<string, nint> _exports = new(StringComparer.Ordinal);

    [StructLayout(LayoutKind.Sequential)]
    public struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        public fixed byte BusId[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryInfo { public ulong Total, Free, Used; }

    private NvmlNative(nint lib) => _lib = lib;

    public static NvmlNative? TryLoad(out string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            reason = "NVML requires Windows.";
            return null;
        }

        var candidates = new[]
        {
            "nvml.dll",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        };
        foreach (var c in candidates)
        {
            if (NativeLibrary.TryLoad(c, out var lib))
            {
                var api = new NvmlNative(lib);
                var init = api.Fn("nvmlInit_v2");
                if (init != 0 && ((delegate* unmanaged[Cdecl]<int>)init)() == Success)
                {
                    reason = string.Empty;
                    return api;
                }

                NativeLibrary.Free(lib);
            }
        }

        reason = "NVML (nvml.dll) not available — NVIDIA driver not installed or too old.";
        return null;
    }

    private nint Fn(string name)
    {
        if (!_exports.TryGetValue(name, out var p))
        {
            p = NativeLibrary.TryGetExport(_lib, name, out var e) ? e : 0;
            _exports[name] = p;
        }

        return p;
    }

    // ---- generic call shapes -------------------------------------------------------------------
    private uint? U(string fn, nint dev)
    {
        var f = Fn(fn);
        if (f == 0) return null;
        uint v;
        return ((delegate* unmanaged[Cdecl]<nint, uint*, int>)f)(dev, &v) == Success ? v : null;
    }

    private string? Str(string fn, nint dev, int size)
    {
        var f = Fn(fn);
        if (f == 0) return null;
        var buf = stackalloc byte[size];
        if (((delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)f)(dev, buf, (uint)size) != Success) return null;
        var span = new ReadOnlySpan<byte>(buf, size);
        var len = span.IndexOf((byte)0);
        var s = Encoding.ASCII.GetString(span[..(len < 0 ? size : len)]).Trim();
        return s.Length == 0 ? null : s;
    }

    public string? DriverVersion()
    {
        var f = Fn("nvmlSystemGetDriverVersion");
        if (f == 0) return null;
        var buf = stackalloc byte[96];
        if (((delegate* unmanaged[Cdecl]<byte*, uint, int>)f)(buf, 96) != Success) return null;
        return Marshal.PtrToStringAnsi((nint)buf);
    }

    public string? NvmlVersion()
    {
        var f = Fn("nvmlSystemGetNVMLVersion");
        if (f == 0) return null;
        var buf = stackalloc byte[96];
        return ((delegate* unmanaged[Cdecl]<byte*, uint, int>)f)(buf, 96) == Success ? Marshal.PtrToStringAnsi((nint)buf) : null;
    }

    public int? CudaDriverVersion()
    {
        var f = Fn("nvmlSystemGetCudaDriverVersion_v2");
        if (f == 0) return null;
        int v;
        return ((delegate* unmanaged[Cdecl]<int*, int>)f)(&v) == Success ? v : null;
    }

    public nint[] Devices()
    {
        var count = Fn("nvmlDeviceGetCount_v2");
        var byIndex = Fn("nvmlDeviceGetHandleByIndex_v2");
        if (count == 0 || byIndex == 0) return [];
        uint n;
        if (((delegate* unmanaged[Cdecl]<uint*, int>)count)(&n) != Success) return [];
        var list = new List<nint>();
        for (uint i = 0; i < n; i++)
        {
            nint h;
            if (((delegate* unmanaged[Cdecl]<uint, nint*, int>)byIndex)(i, &h) == Success) list.Add(h);
        }

        return [.. list];
    }

    public string? Name(nint d) => Str("nvmlDeviceGetName", d, 96);
    public string? Vbios(nint d) => Str("nvmlDeviceGetVbiosVersion", d, 32);
    public string? BoardPartNumber(nint d) => Str("nvmlDeviceGetBoardPartNumber", d, 80);
    public string? Uuid(nint d) => Str("nvmlDeviceGetUUID", d, 96);
    public uint? MemoryBusWidth(nint d) => U("nvmlDeviceGetMemoryBusWidth", d);
    public uint? CurrentPcieGeneration(nint d) => U("nvmlDeviceGetCurrPcieLinkGeneration", d);
    public uint? MaxPcieGeneration(nint d) => U("nvmlDeviceGetMaxPcieLinkGeneration", d);
    public uint? GpuMaxPcieGeneration(nint d) => U("nvmlDeviceGetGpuMaxPcieLinkGeneration", d);
    public uint? CurrentPcieWidth(nint d) => U("nvmlDeviceGetCurrPcieLinkWidth", d);
    public uint? MaxPcieWidth(nint d) => U("nvmlDeviceGetMaxPcieLinkWidth", d);
    public uint? PowerLimitMw(nint d) => U("nvmlDeviceGetPowerManagementLimit", d);
    public uint? DefaultPowerLimitMw(nint d) => U("nvmlDeviceGetPowerManagementDefaultLimit", d);
    public uint? EnforcedPowerLimitMw(nint d) => U("nvmlDeviceGetEnforcedPowerLimit", d);
    public uint? PowerUsageMw(nint d) => U("nvmlDeviceGetPowerUsage", d);
    public uint? Architecture(nint d) => U("nvmlDeviceGetArchitecture", d);
    public uint? CoreCount(nint d) => U("nvmlDeviceGetNumGpuCores", d);

    public (uint Min, uint Max)? PowerLimitConstraintsMw(nint d)
    {
        var f = Fn("nvmlDeviceGetPowerManagementLimitConstraints");
        if (f == 0) return null;
        uint min, max;
        return ((delegate* unmanaged[Cdecl]<nint, uint*, uint*, int>)f)(d, &min, &max) == Success ? (min, max) : null;
    }

    /// <summary>clockType: 0 graphics, 1 SM, 2 memory, 3 video.</summary>
    public uint? MaxClockMhz(nint d, int clockType)
    {
        var f = Fn("nvmlDeviceGetMaxClockInfo");
        if (f == 0) return null;
        uint v;
        return ((delegate* unmanaged[Cdecl]<nint, int, uint*, int>)f)(d, clockType, &v) == Success ? v : null;
    }

    /// <summary>clockId: 0 current, 1 application target, 2 application default, 3 customer boost max.</summary>
    public uint? ClockMhz(nint d, int clockType, int clockId)
    {
        var f = Fn("nvmlDeviceGetClock");
        if (f == 0) return null;
        uint v;
        return ((delegate* unmanaged[Cdecl]<nint, int, int, uint*, int>)f)(d, clockType, clockId, &v) == Success ? v : null;
    }

    /// <summary>thresholdType: 0 shutdown, 1 slowdown, 2 memory max, 3 GPU max operating.</summary>
    public uint? TemperatureThreshold(nint d, int type)
    {
        var f = Fn("nvmlDeviceGetTemperatureThreshold");
        if (f == 0) return null;
        uint v;
        return ((delegate* unmanaged[Cdecl]<nint, int, uint*, int>)f)(d, type, &v) == Success ? v : null;
    }

    public ulong? ClockEventReasons(nint d)
    {
        var f = Fn("nvmlDeviceGetCurrentClocksEventReasons");
        if (f == 0) f = Fn("nvmlDeviceGetCurrentClocksThrottleReasons");
        if (f == 0) return null;
        ulong v;
        return ((delegate* unmanaged[Cdecl]<nint, ulong*, int>)f)(d, &v) == Success ? v : null;
    }

    public int? PerformanceState(nint d)
    {
        var f = Fn("nvmlDeviceGetPerformanceState");
        if (f == 0) return null;
        int v;
        return ((delegate* unmanaged[Cdecl]<nint, int*, int>)f)(d, &v) == Success && v is >= 0 and <= 15 ? v : null;
    }

    /// <summary>counter: 0 TX, 1 RX. Returns KB/s.</summary>
    public uint? PcieThroughputKBps(nint d, int counter)
    {
        var f = Fn("nvmlDeviceGetPcieThroughput");
        if (f == 0) return null;
        uint v;
        return ((delegate* unmanaged[Cdecl]<nint, int, uint*, int>)f)(d, counter, &v) == Success ? v : null;
    }

    public PciInfo? Pci(nint d)
    {
        var f = Fn("nvmlDeviceGetPciInfo_v3");
        if (f == 0) return null;
        PciInfo p;
        return ((delegate* unmanaged[Cdecl]<nint, PciInfo*, int>)f)(d, &p) == Success ? p : null;
    }

    public MemoryInfo? Memory(nint d)
    {
        var f = Fn("nvmlDeviceGetMemoryInfo");
        if (f == 0) return null;
        MemoryInfo m;
        return ((delegate* unmanaged[Cdecl]<nint, MemoryInfo*, int>)f)(d, &m) == Success ? m : null;
    }

    public (int Current, int Pending)? DriverModel(nint d)
    {
        var f = Fn("nvmlDeviceGetDriverModel");
        if (f == 0) return null;
        int c, p;
        return ((delegate* unmanaged[Cdecl]<nint, int*, int*, int>)f)(d, &c, &p) == Success ? (c, p) : null;
    }

    public void Dispose()
    {
        var shutdown = Fn("nvmlShutdown");
        if (shutdown != 0) ((delegate* unmanaged[Cdecl]<int>)shutdown)();
        NativeLibrary.Free(_lib);
    }
}

/// <summary>Decoding helpers for NVML enums / bitmasks (pure, unit-tested).</summary>
public static class NvmlDecode
{
    /// <summary>nvmlClocksEventReasons bits (formerly "throttle reasons").</summary>
    public static IReadOnlyList<(ulong Bit, string Name)> ClockEventReasons { get; } =
    [
        (0x1, "GPU Idle"),
        (0x2, "Applications Clocks Setting"),
        (0x4, "Software Power Cap"),
        (0x8, "Hardware Slowdown"),
        (0x10, "Sync Boost"),
        (0x20, "Software Thermal Slowdown"),
        (0x40, "Hardware Thermal Slowdown"),
        (0x80, "Hardware Power Brake Slowdown"),
        (0x100, "Display Clock Setting"),
    ];

    public static IReadOnlyList<string> ActiveReasons(ulong mask) =>
        ClockEventReasons.Where(r => (mask & r.Bit) != 0).Select(r => r.Name).ToList();

    public static string? Architecture(uint? code) => code switch
    {
        2 => "Kepler",
        3 => "Maxwell",
        4 => "Pascal",
        5 => "Volta",
        6 => "Turing",
        7 => "Ampere",
        8 => "Ada Lovelace",
        9 => "Hopper",
        10 => "Blackwell",
        _ => null,
    };

    public static string? DriverModel(int? code) => code switch { 0 => "WDDM", 1 => "TCC", 2 => "MCDM", _ => null };

    /// <summary>CUDA driver version 12040 → "12.4".</summary>
    public static string CudaVersion(int v) => $"{v / 1000}.{v % 1000 / 10}";
}
