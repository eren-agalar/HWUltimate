using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Hardware;

/// <summary>
/// Windows memory-manager counters (GlobalMemoryStatusEx, GetPerformanceInfo): physical, committed and virtual memory,
/// system cache and kernel pools. Complements LibreHardwareMonitor's memory sensors with commit-charge data.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsMemoryProvider : IHardwareProvider
{
    public const string ProviderName = "Windows";
    private const string DeviceId = "win:memory";
    private const double Gb = 1024d * 1024 * 1024;
    private const double Mb = 1024d * 1024;

    /// <summary>Identity shared with LibreHardwareMonitor's "Total Memory" so both merge into one System Memory device.</summary>
    public const string SystemMemoryKey = "system-memory";

    private static readonly HardwareDevice Device = new(DeviceId, "System Memory", HardwareCategory.Memory, ProviderName,
        Identity: new DeviceIdentity { SingletonKey = SystemMemoryKey });
    private List<RawSensorReading> _readings = [];

    public string Name => ProviderName;
    public int Priority => 30;
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Status = ProviderStatus.Ready("Memory manager counters");
        return Task.CompletedTask;
    }

    public void Update()
    {
        var list = new List<RawSensorReading>(16);
        var ms = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            Add(list, "phys/load", "Memory", SensorType.Utilization, SensorUnit.Percent, ms.MemoryLoad);
            Add(list, "phys/total", "Physical Memory Total", SensorType.Memory, SensorUnit.Gigabyte, ms.TotalPhys / Gb);
            Add(list, "phys/used", "Memory Used", SensorType.Memory, SensorUnit.Gigabyte, (ms.TotalPhys - ms.AvailPhys) / Gb);
            Add(list, "phys/available", "Memory Available", SensorType.Memory, SensorUnit.Gigabyte, ms.AvailPhys / Gb);
        }

        var pi = new PerformanceInformation { Cb = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (GetPerformanceInfo(ref pi, pi.Cb))
        {
            var page = (double)pi.PageSize;
            Add(list, "commit/used", "Committed Memory", SensorType.Memory, SensorUnit.Gigabyte, pi.CommitTotal * page / Gb);
            Add(list, "commit/limit", "Commit Limit (virtual memory)", SensorType.Memory, SensorUnit.Gigabyte, pi.CommitLimit * page / Gb);
            Add(list, "commit/peak", "Commit Peak", SensorType.Memory, SensorUnit.Gigabyte, pi.CommitPeak * page / Gb);
            if (pi.CommitLimit > 0)
            {
                Add(list, "commit/load", "Commit Charge", SensorType.Utilization, SensorUnit.Percent, 100.0 * pi.CommitTotal / pi.CommitLimit);
            }

            Add(list, "commit/available", "Virtual Memory Available", SensorType.Memory, SensorUnit.Gigabyte, (pi.CommitLimit - pi.CommitTotal) * page / Gb);
            Add(list, "cache", "System Cache", SensorType.Memory, SensorUnit.Gigabyte, pi.SystemCache * page / Gb);
            Add(list, "kernel/paged", "Kernel Paged Pool", SensorType.MemorySmall, SensorUnit.Megabyte, pi.KernelPaged * page / Mb);
            Add(list, "kernel/nonpaged", "Kernel Non-paged Pool", SensorType.MemorySmall, SensorUnit.Megabyte, pi.KernelNonpaged * page / Mb);
            Add(list, "processes", "Processes", SensorType.Other, SensorUnit.None, pi.ProcessCount);
            Add(list, "threads", "Threads", SensorType.Other, SensorUnit.None, pi.ThreadCount);
            Add(list, "handles", "Handles", SensorType.Other, SensorUnit.None, pi.HandleCount);
        }

        _readings = list;
    }

    private static void Add(List<RawSensorReading> list, string id, string name, SensorType type, SensorUnit unit, double value) =>
        list.Add(new RawSensorReading($"{DeviceId}/{id}", DeviceId, name, type, unit, value, ProviderName, SensorAvailability.Available, list.Count));

    public IReadOnlyList<RawSensorReading> ReadSensors() => _readings;

    public IReadOnlyList<HardwareDevice> GetDevices() => [Device];

    public void Dispose()
    {
    }
}
