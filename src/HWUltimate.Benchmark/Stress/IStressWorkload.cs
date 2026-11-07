

namespace HWUltimate.Benchmark.Stress;

/// <summary>
/// A deterministic, self-verifying workload. Each iteration recomputes a result whose correct value is known
/// (computed once during <see cref="Prepare"/>); any mismatch indicates a computational error — the hallmark of
/// an unstable overclock, undervolt or failing component.
/// </summary>
public interface IStressWorkload : IDisposable
{
    StressTestKind Kind { get; }

    void Prepare(int workers, StressTestOptions options);

    /// <summary>Runs one iteration for <paramref name="worker"/>. Returns the number of errors detected.</summary>
    int RunIteration(int worker, out string? errorDetail);
}
