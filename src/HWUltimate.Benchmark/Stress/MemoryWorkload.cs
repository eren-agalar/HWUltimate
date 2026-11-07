

namespace HWUltimate.Benchmark.Stress;

/// <summary>
/// User-mode RAM test: writes and verifies classic patterns (solid, checkerboard, walking ones,
/// address-in-address, pseudo-random) over a large pinned allocation split across workers.
/// It cannot test memory that Windows or other processes are using; it complements, not replaces, boot-time testers.
/// </summary>
public sealed class MemoryWorkload : IStressWorkload
{
    internal const int PatternCount = 7;
    private ulong[][] _blocks = [];
    private int[] _pass = [];

    public StressTestKind Kind => StressTestKind.Memory;

    public long AllocatedBytes { get; private set; }

    public static long AutomaticSizeBytes()
    {
        var info = GC.GetGCMemoryInfo();
        var free = Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        var target = (long)(free * 0.6);
        return Math.Clamp(target, 256L << 20, 64L << 30);
    }

    public void Prepare(int workers, StressTestOptions options)
    {
        var total = options.MemoryMegabytes > 0 ? (long)options.MemoryMegabytes << 20 : AutomaticSizeBytes();
        var perWorker = Math.Max(1L << 20, total / workers);
        var words = (int)Math.Min(perWorker / sizeof(ulong), Array.MaxLength);
        _blocks = new ulong[workers][];
        for (var w = 0; w < workers; w++)
        {
            _blocks[w] = GC.AllocateUninitializedArray<ulong>(words, pinned: true);
        }

        _pass = new int[workers];
        AllocatedBytes = (long)words * sizeof(ulong) * workers;
    }

    public int RunIteration(int worker, out string? errorDetail)
    {
        var block = _blocks[worker];
        var pass = _pass[worker]++;
        var pattern = pass % PatternCount;
        var seed = (ulong)(pass + 1) * 0x9E3779B97F4A7C15UL ^ (ulong)worker;

        for (var i = 0; i < block.Length; i++)
        {
            block[i] = Expected(pattern, i, seed);
        }

        var errors = 0;
        errorDetail = null;
        for (var i = 0; i < block.Length; i++)
        {
            var expected = Expected(pattern, i, seed);
            var actual = block[i];
            if (actual != expected)
            {
                errors++;
                errorDetail ??= $"Memory mismatch (pattern {PatternName(pattern)}, worker {worker}, offset 0x{(long)i * 8:X}): " +
                                $"expected 0x{expected:X16}, read 0x{actual:X16}, flipped bits 0x{expected ^ actual:X16}";
            }
        }

        return errors;
    }

    internal static ulong Expected(int pattern, int index, ulong seed) => pattern switch
    {
        0 => 0UL,
        1 => ulong.MaxValue,
        2 => 0xAAAAAAAAAAAAAAAAUL,
        3 => 0x5555555555555555UL,
        4 => 1UL << (index & 63),
        5 => (ulong)index ^ seed,
        _ => Mix((ulong)index + seed),
    };

    internal static string PatternName(int pattern) => pattern switch
    {
        0 => "zeros",
        1 => "ones",
        2 => "checkerboard A",
        3 => "checkerboard 5",
        4 => "walking ones",
        5 => "address",
        _ => "random",
    };

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public void Dispose()
    {
        _blocks = [];
        AllocatedBytes = 0;
        GC.Collect();
    }
}
