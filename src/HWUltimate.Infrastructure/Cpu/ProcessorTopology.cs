using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HWUltimate.Infrastructure.Cpu;

public sealed record CacheInstanceSummary(int Level, string Type, long SizeBytes, int Associativity, int LineSize, int Instances);

/// <summary>Physical topology from GetLogicalProcessorInformationEx (packages, cores, SMT, efficiency classes, caches).</summary>
public sealed record ProcessorTopology(
    int Packages,
    int Cores,
    int LogicalProcessors,
    bool Smt,
    IReadOnlyDictionary<int, int> CoresByEfficiencyClass,
    IReadOnlyList<CacheInstanceSummary> Caches)
{
    /// <summary>On hybrid parts the highest efficiency class denotes performance cores.</summary>
    public (int PCores, int ECores)? HybridSplit =>
        CoresByEfficiencyClass.Count > 1
            ? (CoresByEfficiencyClass[CoresByEfficiencyClass.Keys.Max()], CoresByEfficiencyClass.Where(kv => kv.Key != CoresByEfficiencyClass.Keys.Max()).Sum(kv => kv.Value))
            : null;

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationProcessorPackage = 3;

    /// <summary>Parses the variable-length SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX buffer.</summary>
    public static ProcessorTopology Parse(ReadOnlySpan<byte> buffer)
    {
        int packages = 0, cores = 0, logical = 0;
        var smt = false;
        var classes = new Dictionary<int, int>();
        var caches = new Dictionary<(int, string, long, int, int), int>();
        var offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 4)..]);
            if (size <= 8 || offset + size > buffer.Length) break;
            var entry = buffer.Slice(offset, size);
            switch (relationship)
            {
                case RelationProcessorPackage:
                    packages++;
                    break;
                case RelationProcessorCore when entry.Length >= 40:
                    cores++;
                    var flags = entry[8];
                    var effClass = entry[9];
                    var hasSmt = (flags & 1) != 0;
                    smt |= hasSmt;
                    classes[effClass] = classes.GetValueOrDefault(effClass) + 1;
                    // GROUP_AFFINITY.Mask (KAFFINITY) at offset 32 for the first group.
                    logical += BitCount(BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]));
                    break;
                case RelationCache when entry.Length >= 20:
                    var level = entry[8];
                    var assoc = entry[9];
                    var line = BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]);
                    var bytes = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                    var type = BinaryPrimitives.ReadInt32LittleEndian(entry[16..]) switch { 1 => "Instruction", 2 => "Data", 3 => "Trace", _ => "Unified" };
                    var key = ((int)level, type, (long)bytes, (int)assoc, (int)line);
                    caches[key] = caches.GetValueOrDefault(key) + 1;
                    break;
            }

            offset += size;
        }

        var cacheList = caches.Select(kv => new CacheInstanceSummary(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Key.Item4, kv.Key.Item5, kv.Value))
            .OrderBy(c => c.Level).ThenBy(c => c.Type == "Instruction" ? 0 : c.Type == "Data" ? 1 : 2).ThenByDescending(c => c.SizeBytes).ToList();
        return new ProcessorTopology(packages, cores, logical, smt, classes, cacheList);
    }

    private static int BitCount(ulong v) => System.Numerics.BitOperations.PopCount(v);

    [SupportedOSPlatform("windows")]
    public static unsafe ProcessorTopology? Query()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(0xFFFF, null, &length);
        if (length == 0) return null;
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!GetLogicalProcessorInformationEx(0xFFFF, p, &length)) return null;
        }

        return Parse(buffer.AsSpan(0, (int)length));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, uint* returnedLength);
}
