using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HWUltimate.Core.HardwareInfo;

namespace HWUltimate.Infrastructure.Cpu;

/// <summary>Reads Windows' CPU-set information (GetSystemCpuSetInformation): documented, no driver, no admin rights.</summary>
public static partial class CpuSets
{
    private const int CpuSetInformation = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(nint information, uint bufferLength, out uint returnedLength, nint process, uint flags);

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<CpuSetEntry>? Query()
    {
        GetSystemCpuSetInformation(0, 0, out var needed, 0, 0);
        if (needed == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, needed, out var returned, 0, 0)) return null;
            var bytes = new byte[returned];
            Marshal.Copy(buffer, bytes, 0, (int)returned);
            return Parse(bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Parses SYSTEM_CPU_SET_INFORMATION records: Size (4), Type (4), then CpuSet { Id (4), Group (2),
    /// LogicalProcessorIndex (1), CoreIndex (1), LastLevelCacheIndex (1), NumaNodeIndex (1), EfficiencyClass (1),
    /// AllFlags (1), Reserved / SchedulingClass (4, class in the first byte), AllocationTag (8) }.
    /// </summary>
    public static IReadOnlyList<CpuSetEntry> Parse(ReadOnlySpan<byte> buffer)
    {
        var list = new List<CpuSetEntry>();
        var offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
            var type = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 4)..]);
            if (size < 8 || offset + size > buffer.Length) break;
            if (type == CpuSetInformation && size >= 24)
            {
                var e = buffer.Slice(offset, size);
                list.Add(new CpuSetEntry(
                    Group: BinaryPrimitives.ReadUInt16LittleEndian(e[12..]),
                    LogicalIndex: e[14],
                    CoreIndex: e[15],
                    EfficiencyClass: e[18],
                    SchedulingClass: e[20]));
            }

            offset += size;
        }

        return list;
    }
}
