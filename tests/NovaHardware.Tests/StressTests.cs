using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Sensors;
using NovaHardware.Benchmark.Stress;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;


namespace NovaHardware.Tests;

public class StressWorkloadTests
{
    private static readonly StressTestOptions Opts = new() { Kind = StressTestKind.CpuFloatingPoint, MemoryMegabytes = 8 };

    [Theory]
    [InlineData(StressTestKind.CpuFloatingPoint)]
    [InlineData(StressTestKind.CpuInteger)]
    [InlineData(StressTestKind.CpuVector)]
    [InlineData(StressTestKind.Memory)]
    public void Workloads_are_deterministic_on_healthy_hardware(StressTestKind kind)
    {
        using var w = StressTestService.CreateWorkload(kind, null);
        w.Prepare(2, Opts with { Kind = kind });
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(0, w.RunIteration(i % 2, out var detail));
            Assert.Null(detail);
        }
    }

    [Fact]
    public void Fp_checksum_is_sensitive_to_single_bit_changes()
    {
        var a = FloatingPointWorkload.Seeded(FloatingPointWorkload.N * FloatingPointWorkload.N, 1);
        var b = FloatingPointWorkload.Seeded(FloatingPointWorkload.N * FloatingPointWorkload.N, 2);
        var c = new double[a.Length];
        var golden = FloatingPointWorkload.Compute(a, b, c);
        a[17] = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(a[17]) ^ 1);
        Assert.NotEqual(golden, FloatingPointWorkload.Compute(a, b, c));
    }

    [Fact]
    public void Memory_patterns_cover_classic_cases()
    {
        Assert.Equal(0UL, MemoryWorkload.Expected(0, 5, 0));
        Assert.Equal(ulong.MaxValue, MemoryWorkload.Expected(1, 5, 0));
        Assert.Equal(1UL << 3, MemoryWorkload.Expected(4, 67, 0));
        Assert.NotEqual(MemoryWorkload.Expected(6, 1, 9), MemoryWorkload.Expected(6, 2, 9));
    }

    [Fact]
    public void Memory_workload_allocates_requested_size()
    {
        using var w = new MemoryWorkload();
        w.Prepare(4, Opts with { Kind = StressTestKind.Memory, MemoryMegabytes = 16 });
        Assert.Equal(16L << 20, w.AllocatedBytes);
    }
}

public class StressTestServiceTests
{
    private sealed class HotProvider : IHardwareProvider
    {
        public double Temp { get; set; } = 50;
        public string Name => "hot";
        public int Priority => 0;
        public ProviderStatus Status => ProviderStatus.Ready();
        public Task InitializeAsync(CancellationToken c = default) => Task.CompletedTask;
        public void Update() { }
        public IReadOnlyList<RawSensorReading> ReadSensors() => [new("t", "cpu", "CPU Package", SensorType.Temperature, SensorUnit.Celsius, Temp, Name)];
        public IReadOnlyList<HardwareDevice> GetDevices() => [new("cpu", "CPU", HardwareCategory.Cpu, Name)];
        public void Dispose() { }
    }

    private static (StressTestService Service, SensorPollingService Polling, HotProvider Provider) Create()
    {
        var provider = new HotProvider();
        var polling = new SensorPollingService([provider], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
        polling.PollOnce();
        return (new StressTestService(polling, NullLogger<StressTestService>.Instance), polling, provider);
    }

    [Fact]
    public async Task Short_cpu_run_passes_and_reports_progress()
    {
        var (svc, _, _) = Create();
        var reports = new List<StressTestProgress>();
        var result = await svc.RunAsync(
            new StressTestOptions { Kind = StressTestKind.CpuInteger, Duration = TimeSpan.FromSeconds(1.2), Threads = 2 },
            new SyncProgress<StressTestProgress>(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(StressTestOutcome.Passed, result.Outcome);
        Assert.True(result.Iterations > 0);
        Assert.Equal(0, result.Errors);
        Assert.Equal(50, result.MaxTemperature);
        Assert.NotEmpty(reports);
        Assert.Same(result, svc.Results[0]);
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public async Task Aborts_when_thermal_limit_is_reached()
    {
        var (svc, polling, provider) = Create();
        provider.Temp = 99;
        polling.PollOnce();
        var result = await svc.RunAsync(new StressTestOptions
        {
            Kind = StressTestKind.CpuFloatingPoint, Duration = TimeSpan.FromMinutes(5), Threads = 1, ThermalLimitCelsius = 95,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(StressTestOutcome.AbortedThermal, result.Outcome);
        Assert.Contains("safety limit", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_reported_as_cancelled()
    {
        var (svc, _, _) = Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var result = await svc.RunAsync(new StressTestOptions { Kind = StressTestKind.CpuVector, Duration = TimeSpan.FromMinutes(5), Threads = 1 }, null, cts.Token);
        Assert.Equal(StressTestOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Gpu_test_without_backend_is_not_supported()
    {
        var (svc, _, _) = Create();
        Assert.False(svc.IsSupported(StressTestKind.Gpu, out var reason));
        Assert.NotEmpty(reason);
        var result = await svc.RunAsync(new StressTestOptions { Kind = StressTestKind.Gpu }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(StressTestOutcome.NotSupported, result.Outcome);
    }

    [Fact]
    public async Task Detects_injected_computation_errors()
    {
        var provider = new HotProvider();
        var polling = new SensorPollingService([provider], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
        var svc = new StressTestService(polling, NullLogger<StressTestService>.Instance, new FaultyGpuBackend());
        var result = await svc.RunAsync(new StressTestOptions { Kind = StressTestKind.Gpu, Duration = TimeSpan.FromSeconds(10) },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(StressTestOutcome.Failed, result.Outcome);
        Assert.Equal(1, result.Errors);
        Assert.Single(result.ErrorDetails);
    }

    private sealed class FaultyGpuBackend : IGpuStressBackend
    {
        public bool IsAvailable => true;
        public string Description => "fake";
        public IStressWorkload CreateWorkload() => new Faulty();

        private sealed class Faulty : IStressWorkload
        {
            private int _n;
            public StressTestKind Kind => StressTestKind.Gpu;
            public void Prepare(int workers, StressTestOptions options) { }
            public int RunIteration(int worker, out string? errorDetail)
            {
                Thread.Sleep(5);
                if (++_n == 3) { errorDetail = "bit flip"; return 1; }
                errorDetail = null;
                return 0;
            }
            public void Dispose() { }
        }
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) { lock (this) action(value); }
    }
}
