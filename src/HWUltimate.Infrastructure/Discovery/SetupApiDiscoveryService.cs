using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Hardware;
using HWUltimate.Infrastructure.Smbios;

namespace HWUltimate.Infrastructure.Discovery;

/// <summary>
/// Enumerates present Plug and Play devices through Windows SetupAPI. Used for device classes that WMI
/// does not describe well (USB controllers / hubs, HID, Bluetooth, system devices).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class SetupApiDiscoveryService : IDeviceDiscoveryService
{
    private const uint DigcfPresent = 0x2;
    private const uint DigcfAllClasses = 0x4;
    private const uint SpdrpDeviceDesc = 0x0;
    private const uint SpdrpHardwareId = 0x1;
    private const uint SpdrpClass = 0x7;
    private const uint SpdrpMfg = 0xB;
    private const uint SpdrpFriendlyName = 0xC;
    private const uint SpdrpLocationInformation = 0xD;

    /// <summary>Classes enumerated here. CPU/GPU/disk/network/memory come from WMI and SMBIOS instead.</summary>
    internal static readonly IReadOnlyDictionary<string, HardwareCategory> ClassMap = new Dictionary<string, HardwareCategory>(StringComparer.OrdinalIgnoreCase)
    {
        ["USB"] = HardwareCategory.Usb,
        ["HIDClass"] = HardwareCategory.Usb,
        ["Bluetooth"] = HardwareCategory.Network,
        ["Camera"] = HardwareCategory.Other,
        ["Image"] = HardwareCategory.Other,
        ["Biometric"] = HardwareCategory.Other,
        ["SecurityDevices"] = HardwareCategory.Other,
        ["SCSIAdapter"] = HardwareCategory.Storage,
        ["HDC"] = HardwareCategory.Storage,
        ["SmartCardReader"] = HardwareCategory.Other,
    };

    private readonly ILogger<SetupApiDiscoveryService> _logger;

    public SetupApiDiscoveryService(ILogger<SetupApiDiscoveryService> logger)
    {
        _logger = logger;
    }

    public string SourceName => "SetupAPI";

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial nint SetupDiGetClassDevs(Guid* classGuid, char* enumerator, nint hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(nint set, uint index, SpDevinfoData* data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryProperty(nint set, SpDevinfoData* data, uint property, uint* regType, byte* buffer, uint size, uint* required);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceId(nint set, SpDevinfoData* data, char* buffer, uint size, uint* required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    public Task<IReadOnlyList<DeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DeviceInfo>>(() =>
    {
        var list = new List<DeviceInfo>();
        var set = SetupDiGetClassDevs(null, null, 0, DigcfPresent | DigcfAllClasses);
        if (set == -1)
        {
            _logger.LogWarning("SetupDiGetClassDevs failed with {Error}", Marshal.GetLastPInvokeError());
            return list;
        }

        try
        {
            var data = new SpDevinfoData { CbSize = (uint)sizeof(SpDevinfoData) };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, &data); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cls = Property(set, &data, SpdrpClass);
                if (cls is null || !ClassMap.TryGetValue(cls, out var category))
                {
                    continue;
                }

                var name = Property(set, &data, SpdrpFriendlyName) ?? Property(set, &data, SpdrpDeviceDesc);
                if (name is null)
                {
                    continue;
                }

                var instanceId = InstanceId(set, &data);
                list.Add(new DeviceInfo
                {
                    Id = $"setupapi:{instanceId ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    Name = name,
                    Category = category,
                    Manufacturer = Property(set, &data, SpdrpMfg),
                    Source = SourceName,
                    Properties = SmbiosDiscoveryService.Props(
                        ("Device class", cls),
                        ("Manufacturer", Property(set, &data, SpdrpMfg)),
                        ("Location", Property(set, &data, SpdrpLocationInformation)),
                        ("Hardware ID", Property(set, &data, SpdrpHardwareId)),
                        ("Instance ID", instanceId)),
                });
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return list;
    }, cancellationToken);

    private static string? Property(nint set, SpDevinfoData* data, uint property)
    {
        uint type, required;
        var buffer = stackalloc byte[2048];
        if (!SetupDiGetDeviceRegistryProperty(set, data, property, &type, buffer, 2048, &required))
        {
            return null;
        }

        // REG_SZ or REG_MULTI_SZ: take the first string.
        var s = new string((char*)buffer, 0, (int)Math.Min(required, 2048) / 2);
        var nul = s.IndexOf('\0', StringComparison.Ordinal);
        s = (nul >= 0 ? s[..nul] : s).Trim();
        return s.Length == 0 ? null : s;
    }

    private static string? InstanceId(nint set, SpDevinfoData* data)
    {
        var buffer = stackalloc char[512];
        uint required;
        return SetupDiGetDeviceInstanceId(set, data, buffer, 512, &required) ? new string(buffer) : null;
    }
}
