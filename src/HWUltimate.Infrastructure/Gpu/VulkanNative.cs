using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace HWUltimate.Infrastructure.Gpu;

public sealed record VulkanDeviceInfo(string Name, uint VendorId, uint DeviceId, uint ApiVersion, uint DriverVersion, int DeviceType)
{
    public string ApiVersionText => $"{ApiVersion >> 22}.{(ApiVersion >> 12) & 0x3FF}.{ApiVersion & 0xFFF}";

    /// <summary>Driver version encoding is vendor-specific (NVIDIA and Intel use their own packing).</summary>
    public string DriverVersionText => VendorId switch
    {
        0x10DE => $"{DriverVersion >> 22}.{(DriverVersion >> 14) & 0xFF}",
        0x8086 => $"{DriverVersion >> 14}.{DriverVersion & 0x3FFF}",
        _ => $"{DriverVersion >> 22}.{(DriverVersion >> 12) & 0x3FF}.{DriverVersion & 0xFFF}",
    };
}

/// <summary>Minimal Vulkan loader query: creates an instance and reads VkPhysicalDeviceProperties for each device.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class VulkanNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ApplicationInfo
    {
        public int SType; public nint PNext; public nint PApplicationName; public uint ApplicationVersion;
        public nint PEngineName; public uint EngineVersion; public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public int SType; public nint PNext; public uint Flags; public ApplicationInfo* PApplicationInfo;
        public uint EnabledLayerCount; public nint PpEnabledLayerNames; public uint EnabledExtensionCount; public nint PpEnabledExtensionNames;
    }

    public static IReadOnlyList<VulkanDeviceInfo>? Enumerate(out string? error)
    {
        error = null;
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out var lib))
        {
            error = "Vulkan runtime (vulkan-1.dll) not installed.";
            return null;
        }

        try
        {
            var create = (delegate* unmanaged[Stdcall]<InstanceCreateInfo*, nint, nint*, int>)NativeLibrary.GetExport(lib, "vkCreateInstance");
            var destroy = (delegate* unmanaged[Stdcall]<nint, nint, void>)NativeLibrary.GetExport(lib, "vkDestroyInstance");
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)NativeLibrary.GetExport(lib, "vkEnumeratePhysicalDevices");
            var props = (delegate* unmanaged[Stdcall]<nint, byte*, void>)NativeLibrary.GetExport(lib, "vkGetPhysicalDeviceProperties");

            var name = Marshal.StringToHGlobalAnsi("HWUltimate");
            try
            {
                var app = new ApplicationInfo { SType = 0, PApplicationName = name, PEngineName = name, ApiVersion = (1u << 22) | (1u << 12) };
                var info = new InstanceCreateInfo { SType = 1, PApplicationInfo = &app };
                nint instance;
                var r = create(&info, 0, &instance);
                if (r != 0)
                {
                    error = $"vkCreateInstance failed ({r}).";
                    return null;
                }

                try
                {
                    uint count = 0;
                    enumerate(instance, &count, null);
                    var devices = new nint[count];
                    var list = new List<VulkanDeviceInfo>();
                    fixed (nint* p = devices)
                    {
                        if (count > 0 && enumerate(instance, &count, p) < 0) return list;
                    }

                    var buffer = stackalloc byte[2048]; // VkPhysicalDeviceProperties is 824 bytes
                    foreach (var dev in devices.Take((int)count))
                    {
                        new Span<byte>(buffer, 2048).Clear();
                        props(dev, buffer);
                        var u = (uint*)buffer;
                        var nameSpan = new ReadOnlySpan<byte>(buffer + 20, 256);
                        var len = nameSpan.IndexOf((byte)0);
                        list.Add(new VulkanDeviceInfo(Encoding.UTF8.GetString(nameSpan[..(len < 0 ? 256 : len)]), u[2], u[3], u[0], u[1], (int)u[4]));
                    }

                    return list;
                }
                finally
                {
                    destroy(instance, 0);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }
}
