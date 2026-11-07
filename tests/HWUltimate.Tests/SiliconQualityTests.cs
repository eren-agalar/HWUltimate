using System.Buffers.Binary;
using HWUltimate.Core.HardwareInfo;
using HWUltimate.Domain.Hardware;
using HWUltimate.Infrastructure.Cpu;

namespace HWUltimate.Tests;

public class SiliconQualityTests
{
    private static List<CpuSetEntry> Cpu(int cores, Func<int, int> rank, bool smt = true, Func<int, int>? eff = null)
    {
        var list = new List<CpuSetEntry>();
        var lp = 0;
        for (var c = 0; c < cores; c++)
        {
            for (var t = 0; t < (smt ? 2 : 1); t++) list.Add(new CpuSetEntry(0, lp++, c, eff?.Invoke(c) ?? 0, rank(c)));
        }

        return list;
    }

    private static HardwareProperty Get(IReadOnlyList<HardwareProperty> p, string name) => p.Single(x => x.Name == name);

    [Fact]
    public void Ryzen_style_ranking_lists_best_cores_and_order()
    {
        // 8 cores, SMT, firmware ranks core 3 and 5 best.
        int[] ranks = [140, 142, 138, 150, 141, 150, 139, 137];
        var p = CoreQualityRanking.Properties(Cpu(8, c => ranks[c]));
        Assert.Equal("Core 3, 5", Get(p, "Best cores").Value);
        Assert.StartsWith("3, 5  ›  1  ›  4  ›  0", Get(p, "Core ranking (best first)").Value);
        Assert.Equal("7 levels (137–150)", Get(p, "Ranking levels").Value);
        Assert.False(Get(p, "Silicon quality score").IsAvailable); // never invented
    }

    [Fact]
    public void Hybrid_processors_rank_performance_and_efficiency_cores_separately()
    {
        // 6 P-cores (class 1) with a ranking, 8 E-cores (class 0) all equal.
        var p = CoreQualityRanking.Properties(Cpu(14, c => c < 6 ? (c == 2 || c == 3 ? 9 : 7) : 5, smt: false, eff: c => c < 6 ? 1 : 0));
        Assert.Equal("Core 2, 3", Get(p, "Performance cores — Best cores").Value);
        Assert.False(Get(p, "Efficiency cores — Core quality ranking").IsAvailable);
    }

    [Fact]
    public void No_ranking_reported_is_shown_as_not_available()
    {
        var p = CoreQualityRanking.Properties(Cpu(4, _ => 0));
        Assert.False(Get(p, "Core quality ranking").IsAvailable);
        Assert.DoesNotContain(p, x => x.Name == "Best cores");
        Assert.DoesNotContain(CoreQualityRanking.Properties(null), x => x.IsAvailable);
    }

    [Fact]
    public void Parses_windows_cpu_set_records()
    {
        static byte[] Record(ushort group, byte lp, byte core, byte eff, byte sched)
        {
            var b = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(b, 32);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 256u + lp);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), group);
            b[14] = lp; b[15] = core; b[18] = eff; b[20] = sched;
            return b;
        }

        var buffer = Record(0, 0, 0, 1, 7).Concat(Record(0, 1, 0, 1, 7)).Concat(Record(1, 0, 4, 0, 3)).ToArray();
        var sets = CpuSets.Parse(buffer);
        Assert.Equal(3, sets.Count);
        Assert.Equal(new CpuSetEntry(1, 0, 4, 0, 3), sets[2]);
        Assert.Empty(CpuSets.Parse(new byte[] { 4, 0, 0, 0 })); // truncated buffer is ignored safely
    }
}
