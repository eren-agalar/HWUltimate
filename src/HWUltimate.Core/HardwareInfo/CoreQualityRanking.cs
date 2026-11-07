using System.Globalization;
using HWUltimate.Domain.Hardware;

namespace HWUltimate.Core.HardwareInfo;

/// <summary>One logical processor as Windows describes it in its CPU-set information.</summary>
/// <param name="CoreIndex">Physical core (SMT siblings share it).</param>
/// <param name="EfficiencyClass">Higher = performance core on hybrid processors; all equal elsewhere.</param>
/// <param name="SchedulingClass">
/// Windows' preferred-core class. Windows fills it from the per-core "highest performance" that firmware reports through
/// ACPI CPPC — the factory-fused core ranking behind Intel Turbo Boost Max 3.0 "favored cores" and AMD "preferred cores".
/// Higher = better core. Abstract, unit-less; equal on every core when the firmware does not report a ranking.
/// </param>
public sealed record CpuSetEntry(int Group, int LogicalIndex, int CoreIndex, int EfficiencyClass, int SchedulingClass);

/// <summary>
/// "Silicon quality" for processors, built only from what the hardware reports: the manufacturer's per-core ranking.
/// No percentage score is invented — no public interface exposes one — and nothing is estimated from temperature,
/// voltage or clocks.
/// </summary>
public static class CoreQualityRanking
{
    public const string Section = "Silicon Quality";
    public const string Source = "Windows CPU sets (ACPI CPPC)";

    public static IReadOnlyList<HardwareProperty> Properties(IReadOnlyList<CpuSetEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return [HardwareProperty.Unavailable("Core quality ranking", Section, "Windows did not return CPU-set information on this system.", Source), ScoreUnavailable()];
        }

        // Per physical core: SMT siblings report the same class; take the highest to be safe.
        var cores = entries.GroupBy(e => (e.Group, e.CoreIndex))
            .Select(g => (Core: g.Key.CoreIndex, Group: g.Key.Group, Eff: g.Max(e => e.EfficiencyClass), Rank: g.Max(e => e.SchedulingClass)))
            .OrderBy(c => c.Group).ThenBy(c => c.Core)
            .ToList();
        var multiGroup = cores.Select(c => c.Group).Distinct().Count() > 1;
        string Label(int group, int core) => multiGroup ? $"{group}:{core}" : core.ToString(CultureInfo.InvariantCulture);

        var props = new List<HardwareProperty>();
        var classes = cores.Select(c => c.Eff).Distinct().OrderDescending().ToList();
        var hybrid = classes.Count > 1;
        var anyRanking = false;
        foreach (var eff in classes)
        {
            var set = cores.Where(c => c.Eff == eff).ToList();
            var prefix = !hybrid ? "" : eff == classes[0] ? "Performance cores — " : "Efficiency cores — ";
            var ranks = set.Select(c => c.Rank).Distinct().OrderDescending().ToList();
            if (ranks.Count < 2)
            {
                props.Add(HardwareProperty.Unavailable($"{prefix}Core quality ranking", Section,
                    "All these cores report the same ranking: the firmware does not expose a per-core ranking (or CPPC / preferred cores is disabled in the BIOS)."
                    + " Many processors, especially older and mobile ones, do not have one.", Source));
                continue;
            }

            anyRanking = true;
            var best = set.Where(c => c.Rank == ranks[0]).Select(c => Label(c.Group, c.Core)).ToList();
            props.Add(new HardwareProperty($"{prefix}Best cores", $"Core {string.Join(", ", best)}", Section, Source,
                Note: "Highest factory ranking: these cores reached the highest frequency at the lowest voltage in the manufacturer's test. Windows schedules demanding threads on them first."));
            var order = string.Join("  ›  ", ranks.Select(r => string.Join(", ", set.Where(c => c.Rank == r).Select(c => Label(c.Group, c.Core)))));
            props.Add(new HardwareProperty($"{prefix}Core ranking (best first)", order, Section, Source));
            props.Add(new HardwareProperty($"{prefix}Ranking values", string.Join(", ", set.Select(c => $"{Label(c.Group, c.Core)}: {c.Rank}")), Section, Source,
                Note: "Abstract, unit-less values (higher is better). Differences show order only, not a percentage."));
            props.Add(new HardwareProperty($"{prefix}Ranking levels", string.Create(CultureInfo.InvariantCulture, $"{ranks.Count} levels ({ranks[^1]}–{ranks[0]})"), Section, Source));
        }

        if (!anyRanking && props.Count == 0)
        {
            props.Add(HardwareProperty.Unavailable("Core quality ranking", Section, "No ranking reported.", Source));
        }

        props.Add(ScoreUnavailable());
        return props;
    }

    private static HardwareProperty ScoreUnavailable() => HardwareProperty.Unavailable("Silicon quality score", Section,
        "Intel, AMD and Windows expose no ASIC-quality / leakage percentage for processors (board-specific values such as ASUS \"SP\" are proprietary and not readable here)."
        + " The per-core factory ranking above is the real, readable indicator. HWUltimate never estimates a score.", "CPUID, Windows, ACPI");
}
