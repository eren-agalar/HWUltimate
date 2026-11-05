using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Sensors;
using NovaHardware.Domain.Stress;

namespace NovaHardware.Core.Stress;

/// <summary>Runs self-verifying stress workloads on dedicated threads with thermal protection.</summary>
public sealed class StressTestService : IStressTestService
{
    private const int MaxErrorDetails = 50;

    private readonly ISensorPollingService _polling;
    private readonly ILogger<StressTestService> _logger;
    private readonly IGpuStressBackend? _gpuBackend;
    private readonly List<StressTestResult> _results = [];
    private int _running;

    public StressTestService(ISensorPollingService polling, ILogger<StressTestService> logger, IGpuStressBackend? gpuBackend = null)
    {
        _polling = polling;
        _logger = logger;
        _gpuBackend = gpuBackend;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public IReadOnlyList<StressTestResult> Results { get { lock (_results) return _results.ToList(); } }

    public bool IsSupported(StressTestKind kind, out string reason)
    {
        if (kind == StressTestKind.Gpu)
        {
            if (_gpuBackend is { IsAvailable: true })
            {
                reason = _gpuBackend.Description;
                return true;
            }

            reason = _gpuBackend?.Description ?? "No GPU compute backend is installed in this build.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static IStressWorkload CreateWorkload(StressTestKind kind, IGpuStressBackend? gpu) => kind switch
    {
        StressTestKind.CpuFloatingPoint => new FloatingPointWorkload(),
        StressTestKind.CpuInteger => new IntegerWorkload(),
        StressTestKind.CpuVector => new VectorWorkload(),
        StressTestKind.Memory => new MemoryWorkload(),
        StressTestKind.Gpu when gpu is not null => gpu.CreateWorkload(),
        _ => throw new NotSupportedException($"Stress test {kind} is not supported."),
    };

    public async Task<StressTestResult> RunAsync(
        StressTestOptions options,
        IProgress<StressTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        if (!IsSupported(options.Kind, out var reason))
        {
            return Store(new StressTestResult(options.Kind, StressTestOutcome.NotSupported, startedAt, TimeSpan.Zero, 0, 0, null, [], reason));
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A stress test is already running.");
        }

        try
        {
            return Store(await Task.Run(() => Execute(options, progress, startedAt, cancellationToken), CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private StressTestResult Execute(StressTestOptions options, IProgress<StressTestProgress>? progress, DateTimeOffset startedAt, CancellationToken token)
    {
        var workers = options.Kind == StressTestKind.Gpu ? 1 : options.Threads > 0 ? Math.Min(options.Threads, Environment.ProcessorCount) : Environment.ProcessorCount;
        long iterations = 0;
        long errors = 0;
        double? maxTemp = null;
        var details = new ConcurrentQueue<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var outcome = StressTestOutcome.Passed;
        string? fatal = null;

        IStressWorkload workload;
        try
        {
            workload = CreateWorkload(options.Kind, _gpuBackend);
            workload.Prepare(workers, options);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InsufficientExecutionStackException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogError(ex, "Stress test {Kind} failed to prepare", options.Kind);
            return new StressTestResult(options.Kind, StressTestOutcome.Error, startedAt, TimeSpan.Zero, 0, 0, null, [ex.Message], $"Could not start: {ex.Message}");
        }

        using (workload)
        {
            _logger.LogInformation("Stress test {Kind} started: {Workers} workers, {Duration}", options.Kind, workers, options.Duration);
            var sw = Stopwatch.StartNew();
            var threads = new Thread[workers];
            for (var w = 0; w < workers; w++)
            {
                var worker = w;
                threads[w] = new Thread(() =>
                {
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var e = workload.RunIteration(worker, out var detail);
                            Interlocked.Increment(ref iterations);
                            if (e > 0)
                            {
                                Interlocked.Add(ref errors, e);
                                if (detail is not null && details.Count < MaxErrorDetails)
                                {
                                    details.Enqueue($"[{sw.Elapsed:hh\\:mm\\:ss}] {detail}");
                                }

                                if (options.StopOnError)
                                {
                                    stop.Cancel();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // An exception inside a verified workload (e.g. access violation surfaced as SEHException) is itself an instability signal.
                        Interlocked.Increment(ref errors);
                        details.Enqueue($"[{sw.Elapsed:hh\\:mm\\:ss}] Worker {worker} crashed: {ex.GetType().Name}: {ex.Message}");
                        stop.Cancel();
                    }
                })
                {
                    IsBackground = true,
                    Name = $"Nova Stress {options.Kind} #{worker}",
                    // Slightly below normal keeps the UI responsive while still saturating the cores.
                    Priority = ThreadPriority.BelowNormal,
                };
                threads[w].Start();
            }

            var lastIterations = 0L;
            var lastTick = sw.Elapsed;
            while (!stop.IsCancellationRequested && sw.Elapsed < options.Duration)
            {
                stop.Token.WaitHandle.WaitOne(500);
                var temp = _polling.Current.MaxProcessorTemperature();
                if (temp is { } t)
                {
                    maxTemp = maxTemp is null ? t : Math.Max(maxTemp.Value, t);
                    if (options.ThermalLimitCelsius > 0 && t >= options.ThermalLimitCelsius)
                    {
                        outcome = StressTestOutcome.AbortedThermal;
                        fatal = $"Aborted: temperature {t:0.#} °C reached the safety limit of {options.ThermalLimitCelsius:0.#} °C.";
                        stop.Cancel();
                    }
                }

                var now = sw.Elapsed;
                var it = Interlocked.Read(ref iterations);
                var rate = (it - lastIterations) / Math.Max(0.001, (now - lastTick).TotalSeconds);
                lastIterations = it;
                lastTick = now;
                progress?.Report(new StressTestProgress(now, options.Duration, it, Interlocked.Read(ref errors), rate, temp, null));
            }

            stop.Cancel();
            foreach (var thread in threads)
            {
                thread.Join(TimeSpan.FromSeconds(30));
            }

            sw.Stop();
            var errorCount = Interlocked.Read(ref errors);
            if (outcome != StressTestOutcome.AbortedThermal)
            {
                outcome = errorCount > 0 ? StressTestOutcome.Failed
                    : token.IsCancellationRequested ? StressTestOutcome.Cancelled
                    : StressTestOutcome.Passed;
            }

            var summary = fatal ?? outcome switch
            {
                StressTestOutcome.Passed => $"Passed: {iterations:N0} verified iterations in {sw.Elapsed:hh\\:mm\\:ss} with no errors.",
                StressTestOutcome.Failed => $"FAILED: {errorCount:N0} computational error(s) detected after {sw.Elapsed:hh\\:mm\\:ss}. The system is not stable at current settings.",
                StressTestOutcome.Cancelled => $"Cancelled by user after {sw.Elapsed:hh\\:mm\\:ss}; {iterations:N0} iterations, no errors so far.",
                _ => outcome.ToString(),
            };

            progress?.Report(new StressTestProgress(sw.Elapsed, options.Duration, iterations, errorCount, 0, maxTemp, summary));
            _logger.LogInformation("Stress test {Kind} finished: {Outcome}. {Summary}", options.Kind, outcome, summary);
            return new StressTestResult(options.Kind, outcome, startedAt, sw.Elapsed, iterations, errorCount, maxTemp, details.ToList(), summary);
        }
    }

    private StressTestResult Store(StressTestResult result)
    {
        lock (_results)
        {
            _results.Insert(0, result);
        }

        return result;
    }
}

/// <summary>Pluggable GPU compute back-end (e.g. Direct3D 11 compute shaders) for GPU stability testing.</summary>
public interface IGpuStressBackend
{
    bool IsAvailable { get; }
    string Description { get; }
    IStressWorkload CreateWorkload();
}
