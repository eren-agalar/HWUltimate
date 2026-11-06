using System.Runtime.InteropServices;
using System.Text;

namespace NovaHardware.Hardware.Nvidia;

/// <summary>
/// Minimal, documented subset of the public NVIDIA NVAPI resolved through <c>nvapi_QueryInterface</c>.
/// Every function is optional: if the driver does not expose it, the corresponding sensors are simply not reported.
/// </summary>
internal sealed unsafe class NvApiNative : IDisposable
{
    public const int MaxPhysicalGpus = 64;
    public const int ShortStringMax = 64;
    public const int Ok = 0;

    private const uint IdInitialize = 0x0150E828;
    private const uint IdUnload = 0xD22BDD7E;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGpuGetFullName = 0xCEEE8E9F;
    private const uint IdGpuGetThermalSettings = 0xE3640A56;
    private const uint IdGpuGetDynamicPstatesInfoEx = 0x60DED2ED;
    private const uint IdGpuGetTachReading = 0x5F608315;
    private const uint IdGpuGetAllClockFrequencies = 0xDCB616C3;
    private const uint IdSysGetDriverAndBranchVersion = 0x2926AAAD;
    private const uint IdGpuGetBusId = 0x1BE0B8E5;
    private const uint IdGpuGetBusSlotId = 0x2A0A350F;
    private const uint IdGpuGetPciIdentifiers = 0x2DDFB66E;

    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<int> _initialize;
    private readonly delegate* unmanaged[Cdecl]<int> _unload;
    private readonly delegate* unmanaged[Cdecl]<nint*, int*, int> _enumGpus;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, int> _getFullName;
    private readonly delegate* unmanaged[Cdecl]<nint, int, NvThermalSettings*, int> _getThermal;
    private readonly delegate* unmanaged[Cdecl]<nint, NvDynamicPstatesInfoEx*, int> _getPstates;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, int> _getTach;
    private readonly delegate* unmanaged[Cdecl]<nint, NvClockFrequencies*, int> _getClocks;
    private readonly delegate* unmanaged[Cdecl]<uint*, byte*, int> _getDriverVersion;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, int> _getBusId;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, int> _getBusSlotId;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, uint*, uint*, uint*, int> _getPciIdentifiers;

    private NvApiNative(nint library, delegate* unmanaged[Cdecl]<uint, nint> query)
    {
        _library = library;
        _initialize = (delegate* unmanaged[Cdecl]<int>)query(IdInitialize);
        _unload = (delegate* unmanaged[Cdecl]<int>)query(IdUnload);
        _enumGpus = (delegate* unmanaged[Cdecl]<nint*, int*, int>)query(IdEnumPhysicalGpus);
        _getFullName = (delegate* unmanaged[Cdecl]<nint, byte*, int>)query(IdGpuGetFullName);
        _getThermal = (delegate* unmanaged[Cdecl]<nint, int, NvThermalSettings*, int>)query(IdGpuGetThermalSettings);
        _getPstates = (delegate* unmanaged[Cdecl]<nint, NvDynamicPstatesInfoEx*, int>)query(IdGpuGetDynamicPstatesInfoEx);
        _getTach = (delegate* unmanaged[Cdecl]<nint, uint*, int>)query(IdGpuGetTachReading);
        _getClocks = (delegate* unmanaged[Cdecl]<nint, NvClockFrequencies*, int>)query(IdGpuGetAllClockFrequencies);
        _getDriverVersion = (delegate* unmanaged[Cdecl]<uint*, byte*, int>)query(IdSysGetDriverAndBranchVersion);
        _getBusId = (delegate* unmanaged[Cdecl]<nint, uint*, int>)query(IdGpuGetBusId);
        _getBusSlotId = (delegate* unmanaged[Cdecl]<nint, uint*, int>)query(IdGpuGetBusSlotId);
        _getPciIdentifiers = (delegate* unmanaged[Cdecl]<nint, uint*, uint*, uint*, uint*, int>)query(IdGpuGetPciIdentifiers);
    }

    /// <summary>Loads nvapi64.dll and calls NvAPI_Initialize. Returns null with a reason when unavailable.</summary>
    public static NvApiNative? TryLoad(out string reason)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            reason = "NVAPI requires 64-bit Windows.";
            return null;
        }

        if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib))
        {
            reason = "NVIDIA driver (nvapi64.dll) not found.";
            return null;
        }

        if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var queryPtr))
        {
            NativeLibrary.Free(lib);
            reason = "nvapi_QueryInterface export missing.";
            return null;
        }

        var api = new NvApiNative(lib, (delegate* unmanaged[Cdecl]<uint, nint>)queryPtr);
        if (api._initialize == null || api._initialize() != Ok)
        {
            api.Dispose();
            reason = "NvAPI_Initialize failed (no NVIDIA GPU or driver not running).";
            return null;
        }

        reason = string.Empty;
        return api;
    }

    public nint[] EnumPhysicalGpus()
    {
        if (_enumGpus == null)
        {
            return [];
        }

        var handles = stackalloc nint[MaxPhysicalGpus];
        int count;
        return _enumGpus(handles, &count) == Ok ? new Span<nint>(handles, count).ToArray() : [];
    }

    public string? GetFullName(nint gpu)
    {
        if (_getFullName == null)
        {
            return null;
        }

        var buffer = stackalloc byte[ShortStringMax];
        return _getFullName(gpu, buffer) == Ok ? AnsiString(buffer) : null;
    }

    public string? GetDriverVersion()
    {
        if (_getDriverVersion == null)
        {
            return null;
        }

        uint version;
        var branch = stackalloc byte[ShortStringMax];
        return _getDriverVersion(&version, branch) == Ok ? $"{version / 100}.{version % 100:00} ({AnsiString(branch)})" : null;
    }

    public bool TryGetThermal(nint gpu, out NvThermalSettings settings)
    {
        settings = default;
        if (_getThermal == null)
        {
            return false;
        }

        settings.Version = NvThermalSettings.StructVersion;
        fixed (NvThermalSettings* p = &settings)
        {
            return _getThermal(gpu, NvThermalSettings.TargetAll, p) == Ok;
        }
    }

    public bool TryGetUtilization(nint gpu, out NvDynamicPstatesInfoEx info)
    {
        info = default;
        if (_getPstates == null)
        {
            return false;
        }

        info.Version = NvDynamicPstatesInfoEx.StructVersion;
        fixed (NvDynamicPstatesInfoEx* p = &info)
        {
            return _getPstates(gpu, p) == Ok;
        }
    }

    public uint? GetTachRpm(nint gpu)
    {
        if (_getTach == null)
        {
            return null;
        }

        uint rpm;
        return _getTach(gpu, &rpm) == Ok ? rpm : null;
    }

    public uint? GetBusId(nint gpu)
    {
        if (_getBusId == null) return null;
        uint bus;
        return _getBusId(gpu, &bus) == Ok ? bus : null;
    }

    public uint? GetBusSlotId(nint gpu)
    {
        if (_getBusSlotId == null) return null;
        uint slot;
        return _getBusSlotId(gpu, &slot) == Ok ? slot : null;
    }

    /// <summary>NvAPI_GPU_GetPCIIdentifiers: deviceId = (device &lt;&lt; 16) | vendor, subSystemId = (subdevice &lt;&lt; 16) | subvendor.</summary>
    public (uint DeviceId, uint SubSystemId, uint RevisionId)? GetPciIdentifiers(nint gpu)
    {
        if (_getPciIdentifiers == null) return null;
        uint device, subsystem, revision, ext;
        return _getPciIdentifiers(gpu, &device, &subsystem, &revision, &ext) == Ok ? (device, subsystem, revision) : null;
    }

    public bool TryGetCurrentClocks(nint gpu, out NvClockFrequencies clocks) => TryGetClocks(gpu, 0, out clocks);

    /// <summary>clockType: 0 = current, 1 = base, 2 = boost (NV_GPU_CLOCK_FREQUENCIES_CLOCK_TYPE).</summary>
    public bool TryGetClocks(nint gpu, uint clockType, out NvClockFrequencies clocks)
    {
        clocks = default;
        if (_getClocks == null)
        {
            return false;
        }

        clocks.Version = NvClockFrequencies.StructVersion;
        clocks.ClockType = clockType;
        fixed (NvClockFrequencies* p = &clocks)
        {
            return _getClocks(gpu, p) == Ok;
        }
    }

    private static string AnsiString(byte* buffer)
    {
        var len = 0;
        while (len < ShortStringMax && buffer[len] != 0) len++;
        return Encoding.ASCII.GetString(buffer, len).Trim();
    }

    public void Dispose()
    {
        if (_unload != null)
        {
            _unload();
        }

        NativeLibrary.Free(_library);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvThermalSensor
{
    public int Controller;
    public int DefaultMinTemp;
    public int DefaultMaxTemp;
    public int CurrentTemp;
    public NvThermalTarget Target;
}

internal enum NvThermalTarget
{
    None = 0,
    Gpu = 1,
    Memory = 2,
    PowerSupply = 4,
    Board = 8,
    VcdBoard = 9,
    VcdInlet = 10,
    VcdOutlet = 11,
    All = 15,
    Unknown = -1,
}

/// <summary>NV_GPU_THERMAL_SETTINGS_V2.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NvThermalSettings
{
    public const int MaxSensors = 3;
    public const int TargetAll = 15;
    public static readonly uint StructVersion = (uint)sizeof(NvThermalSettings) | (2u << 16);

    public uint Version;
    public uint Count;
    public NvThermalSensor Sensor0;
    public NvThermalSensor Sensor1;
    public NvThermalSensor Sensor2;

    public readonly NvThermalSensor this[int i] => i switch { 0 => Sensor0, 1 => Sensor1, _ => Sensor2 };
}

/// <summary>NV_GPU_DYNAMIC_PSTATES_INFO_EX.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NvDynamicPstatesInfoEx
{
    public const int MaxDomains = 8;
    public static readonly uint StructVersion = (uint)sizeof(NvDynamicPstatesInfoEx) | (1u << 16);

    public uint Version;
    public uint Flags;
    public fixed uint Domains[MaxDomains * 2]; // { bIsPresent, percentage } pairs

    public readonly bool IsPresent(int domain) => (Domains[domain * 2] & 1) != 0;
    public readonly uint Percentage(int domain) => Domains[domain * 2 + 1];
}

/// <summary>NV_GPU_CLOCK_FREQUENCIES_V2. Frequencies are reported by the driver in kHz.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NvClockFrequencies
{
    public const int MaxDomains = 32;
    public const int Graphics = 0;
    public const int Memory = 4;
    public const int Processor = 7;
    public const int Video = 8;
    public static readonly uint StructVersion = (uint)sizeof(NvClockFrequencies) | (2u << 16);

    public uint Version;
    public uint ClockType;
    public fixed uint Domains[MaxDomains * 2]; // { bIsPresent, frequency kHz } pairs

    public readonly bool IsPresent(int domain) => (Domains[domain * 2] & 1) != 0;
    public readonly uint FrequencyKhz(int domain) => Domains[domain * 2 + 1];
}
