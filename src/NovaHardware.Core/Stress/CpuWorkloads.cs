using System.Numerics;
using NovaHardware.Domain.Stress;

namespace NovaHardware.Core.Stress;

/// <summary>Dense double-precision matrix multiply plus transcendental functions, verified bit-exactly.</summary>
public sealed class FloatingPointWorkload : IStressWorkload
{
    internal const int N = 64;
    private double[][] _a = [];
    private double[][] _b = [];
    private double[][] _c = [];
    private ulong _golden;

    public StressTestKind Kind => StressTestKind.CpuFloatingPoint;

    public void Prepare(int workers, StressTestOptions options)
    {
        _a = new double[workers][];
        _b = new double[workers][];
        _c = new double[workers][];
        for (var w = 0; w < workers; w++)
        {
            _a[w] = Seeded(N * N, 0x1234);
            _b[w] = Seeded(N * N, 0x9876);
            _c[w] = new double[N * N];
        }

        _golden = Compute(_a[0], _b[0], _c[0]);
    }

    public int RunIteration(int worker, out string? errorDetail)
    {
        var result = Compute(_a[worker], _b[worker], _c[worker]);
        if (result == _golden)
        {
            errorDetail = null;
            return 0;
        }

        errorDetail = $"FP checksum mismatch on worker {worker}: expected 0x{_golden:X16}, got 0x{result:X16}";
        return 1;
    }

    internal static double[] Seeded(int length, ulong seed)
    {
        var data = new double[length];
        var x = seed;
        for (var i = 0; i < length; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            data[i] = (x % 10_000) / 997.0 - 5.0;
        }

        return data;
    }

    internal static ulong Compute(double[] a, double[] b, double[] c)
    {
        for (var i = 0; i < N; i++)
        {
            for (var j = 0; j < N; j++)
            {
                double sum = 0;
                for (var k = 0; k < N; k++)
                {
                    sum += a[i * N + k] * b[k * N + j];
                }

                c[i * N + j] = sum;
            }
        }

        ulong hash = 1469598103934665603UL;
        for (var i = 0; i < c.Length; i++)
        {
            var v = Math.Sqrt(Math.Abs(c[i]) + 1.0) + Math.Sin(c[i] * 0.001);
            hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(v)) * 1099511628211UL;
        }

        return hash;
    }

    public void Dispose()
    {
    }
}

/// <summary>Integer ALU / branch / cache workload: PRNG stream, mixing and hashing over a 256 KiB buffer.</summary>
public sealed class IntegerWorkload : IStressWorkload
{
    internal const int Length = 32 * 1024; // 32K × 8 bytes = 256 KiB (fits L2 on most CPUs)
    private ulong[][] _buffers = [];
    private ulong _golden;

    public StressTestKind Kind => StressTestKind.CpuInteger;

    public void Prepare(int workers, StressTestOptions options)
    {
        _buffers = Enumerable.Range(0, workers).Select(_ => new ulong[Length]).ToArray();
        _golden = Compute(_buffers[0]);
    }

    public int RunIteration(int worker, out string? errorDetail)
    {
        var result = Compute(_buffers[worker]);
        if (result == _golden)
        {
            errorDetail = null;
            return 0;
        }

        errorDetail = $"Integer checksum mismatch on worker {worker}: expected 0x{_golden:X16}, got 0x{result:X16}";
        return 1;
    }

    internal static ulong Compute(ulong[] buffer)
    {
        ulong x = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < buffer.Length; i++)
        {
            x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
            buffer[i] = x * 2685821657736338717UL;
        }

        ulong h = 0xCBF29CE484222325UL;
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                var v = buffer[(i * 7919 + round) & (Length - 1)];
                h ^= BitOperations.RotateLeft(v, (int)(h & 63)) + (ulong)BitOperations.PopCount(v);
                h *= 0x100000001B3UL;
                if ((v & 1) == 0)
                {
                    h += (ulong)i;
                }
                else
                {
                    h -= v >> 3;
                }
            }
        }

        return h;
    }

    public void Dispose()
    {
    }
}

/// <summary>SIMD workload using <see cref="Vector{T}"/> (SSE / AVX2 / AVX-512 depending on the CPU).</summary>
public sealed class VectorWorkload : IStressWorkload
{
    internal const int Length = 16 * 1024;
    private float[] _seedA = [];
    private float[] _seedB = [];
    private float[][] _work = [];
    private ulong _golden;

    public StressTestKind Kind => StressTestKind.CpuVector;

    public static string InstructionWidth => $"{Vector<float>.Count * 32}-bit ({(Vector.IsHardwareAccelerated ? "hardware accelerated" : "software")})";

    public void Prepare(int workers, StressTestOptions options)
    {
        _seedA = FloatingPointWorkload.Seeded(Length, 0xABCDEF).Select(d => (float)d).ToArray();
        _seedB = FloatingPointWorkload.Seeded(Length, 0x13579B).Select(d => (float)(d * 0.01)).ToArray();
        _work = Enumerable.Range(0, workers).Select(_ => new float[Length]).ToArray();
        _golden = Compute(_seedA, _seedB, _work[0]);
    }

    public int RunIteration(int worker, out string? errorDetail)
    {
        var result = Compute(_seedA, _seedB, _work[worker]);
        if (result == _golden)
        {
            errorDetail = null;
            return 0;
        }

        errorDetail = $"SIMD checksum mismatch on worker {worker}: expected 0x{_golden:X16}, got 0x{result:X16}";
        return 1;
    }

    internal static ulong Compute(float[] seedA, float[] seedB, float[] work)
    {
        seedA.CopyTo(work, 0);
        var width = Vector<float>.Count;
        var half = new Vector<float>(0.5f);
        var one = new Vector<float>(1.0f);
        for (var pass = 0; pass < 64; pass++)
        {
            for (var i = 0; i <= work.Length - width; i += width)
            {
                var a = new Vector<float>(work, i);
                var b = new Vector<float>(seedB, i);
                // Bounded recurrence so values never overflow to Inf.
                var r = (a * b + one) * half;
                r = Vector.SquareRoot(Vector.Abs(r) + one) - half;
                r.CopyTo(work, i);
            }
        }

        ulong h = 0xCBF29CE484222325UL;
        foreach (var f in work)
        {
            h = (h ^ BitConverter.SingleToUInt32Bits(f)) * 0x100000001B3UL;
        }

        return h;
    }

    public void Dispose()
    {
    }
}
