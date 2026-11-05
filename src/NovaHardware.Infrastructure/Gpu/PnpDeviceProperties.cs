using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NovaHardware.Infrastructure.Gpu;

/// <summary>PnP properties of a PCI device read via SetupDiGetDevicePropertyW (unified property model).</summary>
public sealed record PciDeviceProperties(
    string InstanceId,
    uint? BusNumber, uint? Address, uint? CurrentLinkSpeed, uint? CurrentLinkWidth, uint? MaxLinkSpeed, uint? MaxLinkWidth,
    string? DriverKey, string? LocationInfo)
{
    public uint? Device => Address is { } a ? a >> 16 : null;
    public uint? Function => Address is { } a ? a & 0xFFFF : null;
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class PnpDeviceProperties
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid Fmtid; public uint Pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData { public uint CbSize; public Guid ClassGuid; public uint DevInst; public nint Reserved; }

    private static readonly Guid DeviceBase = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid PciDevice = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    private static readonly DevPropKey BusNumber = new() { Fmtid = DeviceBase, Pid = 23 };
    private static readonly DevPropKey Address = new() { Fmtid = DeviceBase, Pid = 30 };
    private static readonly DevPropKey CurLinkSpeed = new() { Fmtid = PciDevice, Pid = 9 };
    private static readonly DevPropKey CurLinkWidth = new() { Fmtid = PciDevice, Pid = 10 };
    private static readonly DevPropKey MaxLinkSpeed = new() { Fmtid = PciDevice, Pid = 11 };
    private static readonly DevPropKey MaxLinkWidth = new() { Fmtid = PciDevice, Pid = 12 };

    private const uint DigcfPresent = 0x2, DigcfAllClasses = 0x4;
    private const uint SpdrpDriver = 0x9, SpdrpLocationInformation = 0xD, SpdrpCompatibleIds = 0x2, SpdrpDeviceDesc = 0x0, SpdrpFriendlyName = 0xC;

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial nint SetupDiGetClassDevs(Guid* classGuid, char* enumerator, nint hwnd, uint flags);
    [LibraryImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(nint set, uint index, SpDevinfoData* data);
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceId(nint set, SpDevinfoData* data, char* buffer, uint size, uint* required);
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceProperty(nint set, SpDevinfoData* data, DevPropKey* key, uint* type, byte* buffer, uint size, uint* required, uint flags);
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryProperty(nint set, SpDevinfoData* data, uint property, uint* regType, byte* buffer, uint size, uint* required);
    [LibraryImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    /// <summary>Reads PCI properties for every present device whose instance id starts with PCI\.</summary>
    public static IReadOnlyDictionary<string, PciDeviceProperties> ReadPciDevices()
    {
        var result = new Dictionary<string, PciDeviceProperties>(StringComparer.OrdinalIgnoreCase);
        ForEachDevice((set, data, id) =>
        {
            if (!id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return;
            result[id] = new PciDeviceProperties(id,
                UInt(set, data, BusNumber), UInt(set, data, Address),
                UInt(set, data, CurLinkSpeed), UInt(set, data, CurLinkWidth), UInt(set, data, MaxLinkSpeed), UInt(set, data, MaxLinkWidth),
                RegString(set, data, SpdrpDriver), RegString(set, data, SpdrpLocationInformation));
        });
        return result;
    }

    /// <summary>Finds PCI bridge devices whose compatible ids contain the given class code (e.g. "CC_0601" = ISA/LPC bridge).</summary>
    public static IReadOnlyList<(string Name, string InstanceId)> FindByClassCode(string classCode)
    {
        var list = new List<(string, string)>();
        ForEachDevice((set, data, id) =>
        {
            if (!id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return;
            var compat = RegMultiString(set, data, SpdrpCompatibleIds);
            if (compat.Any(c => c.Contains(classCode, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add((RegString(set, data, SpdrpFriendlyName) ?? RegString(set, data, SpdrpDeviceDesc) ?? id, id));
            }
        });
        return list;
    }

    private static void ForEachDevice(Action<nint, nint, string> action)
    {
        var set = SetupDiGetClassDevs(null, null, 0, DigcfPresent | DigcfAllClasses);
        if (set == -1) return;
        try
        {
            var data = new SpDevinfoData { CbSize = (uint)sizeof(SpDevinfoData) };
            var idBuf = stackalloc char[512];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, &data); i++)
            {
                uint req;
                if (!SetupDiGetDeviceInstanceId(set, &data, idBuf, 512, &req)) continue;
                action(set, (nint)(&data), new string(idBuf));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static uint? UInt(nint set, nint data, DevPropKey key)
    {
        uint type, req, value;
        return SetupDiGetDeviceProperty(set, (SpDevinfoData*)data, &key, &type, (byte*)&value, 4, &req, 0) && req == 4 ? value : null;
    }

    private static string? RegString(nint set, nint data, uint prop) => RegMultiString(set, data, prop).FirstOrDefault();

    private static List<string> RegMultiString(nint set, nint data, uint prop)
    {
        uint type, req;
        var buf = stackalloc byte[4096];
        if (!SetupDiGetDeviceRegistryProperty(set, (SpDevinfoData*)data, prop, &type, buf, 4096, &req)) return [];
        return new string((char*)buf, 0, (int)Math.Min(req, 4096) / 2).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
