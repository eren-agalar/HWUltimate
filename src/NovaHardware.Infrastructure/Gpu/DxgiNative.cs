using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NovaHardware.Infrastructure.Gpu;

public sealed record DxgiAdapterInfo(
    string Description, uint VendorId, uint DeviceId, uint SubSysId, uint Revision,
    ulong DedicatedVideoMemory, ulong DedicatedSystemMemory, ulong SharedSystemMemory, bool IsSoftware,
    int? D3D12FeatureLevel, int? D3D11FeatureLevel);

/// <summary>Enumerates DXGI adapters and probes the highest Direct3D 12 / 11 feature level per adapter.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class DxgiNative
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidD3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [LibraryImport("d3d12.dll")]
    private static partial int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, in Guid riid, nint ppDevice);

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* levels, uint count, uint sdkVersion, nint ppDevice, int* featureLevel, nint ppContext);

    private static readonly int[] D3D12Levels = [0xc200, 0xc100, 0xc000, 0xb100, 0xb000];
    private static readonly int[] D3D11Levels = [0xb100, 0xb000, 0xa100, 0xa000, 0x9300];

    public static IReadOnlyList<DxgiAdapterInfo> Enumerate()
    {
        var list = new List<DxgiAdapterInfo>();
        if (CreateDXGIFactory1(IidFactory1, out var factory) < 0 || factory == 0) return list;
        try
        {
            var fvt = *(nint**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)fvt[12];
            for (uint i = 0; i < 16; i++)
            {
                nint adapter;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0) break;
                try
                {
                    var avt = *(nint**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)avt[10];
                    AdapterDesc1 d;
                    if (getDesc1(adapter, &d) < 0) continue;
                    var software = (d.Flags & 2) != 0;
                    list.Add(new DxgiAdapterInfo(new string(d.Description).TrimEnd('\0'), d.VendorId, d.DeviceId, d.SubSysId, d.Revision,
                        d.DedicatedVideoMemory, d.DedicatedSystemMemory, d.SharedSystemMemory, software,
                        software ? null : ProbeD3D12(adapter), software ? null : ProbeD3D11(adapter)));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return list;
    }

    private static int? ProbeD3D12(nint adapter)
    {
        try
        {
            // Passing a null device pointer only tests support: S_FALSE (1) means the level is supported.
            foreach (var level in D3D12Levels)
            {
                if (D3D12CreateDevice(adapter, level, IidD3D12Device, 0) >= 0) return level;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    private static int? ProbeD3D11(nint adapter)
    {
        try
        {
            fixed (int* levels = D3D11Levels)
            {
                int achieved;
                // D3D_DRIVER_TYPE_UNKNOWN (0) is required with an explicit adapter; null device = feature-level query only.
                var hr = D3D11CreateDevice(adapter, 0, 0, 0, levels, (uint)D3D11Levels.Length, 7, 0, &achieved, 0);
                if (hr >= 0) return achieved;
                hr = D3D11CreateDevice(adapter, 0, 0, 0, levels + 1, (uint)D3D11Levels.Length - 1, 7, 0, &achieved, 0); // pre-11.1 runtimes
                return hr >= 0 ? achieved : null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown == 0) return;
        var vt = *(nint**)unknown;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vt[2])(unknown);
    }

    public static string FeatureLevelText(int level) => $"{level >> 12}_{(level >> 8) & 0xF}";
}
