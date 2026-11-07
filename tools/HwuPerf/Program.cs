using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using HWUltimate.Core.Abstractions;
using HWUltimate.Core.Explorer;
using HWUltimate.Core.Sensors;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

// Synthetic provider: 12 devices × N sensors with changing values.
var sensorsPerDevice = args.Length > 0 ? int.Parse(args[0]) : 40;
var provider = new SyntheticProvider(12, sensorsPerDevice);
var polling = new SensorPollingService([provider], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
await polling.InitializeAsync();
var state = new SensorExplorerState();

Measure("PollOnce", 2000, () => polling.PollOnce());
var snap = polling.PollOnce();
Measure("Explorer.Build", 2000, () => SensorExplorerBuilder.Build(snap, state));
Measure("Explorer.Build (search)", 2000, () => SensorExplorerBuilder.Build(snap, state, new ExplorerFilter("Temp")));
Console.WriteLine($"sensors={snap.Sensors.Count} devices={snap.Devices.Count}");

static void Measure(string name, int iterations, Action action)
{
    for (var i = 0; i < 200; i++) action();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    var alloc0 = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iterations; i++) action();
    sw.Stop();
    var alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
    Console.WriteLine($"{name,-26} {sw.Elapsed.TotalMicroseconds / iterations,9:0.0} µs/op {alloc / iterations / 1024.0,9:0.0} KB/op");
}

sealed class SyntheticProvider(int devices, int perDevice) : IHardwareProvider
{
    private static readonly SensorType[] Types = [SensorType.Temperature, SensorType.Voltage, SensorType.Power, SensorType.Clock, SensorType.Utilization, SensorType.FanSpeed];
    private readonly List<RawSensorReading> _readings = [];
    private readonly List<HardwareDevice> _devices = Enumerable.Range(0, devices)
        .Select(d => new HardwareDevice($"dev{d}", $"Device {d}", (HardwareCategory)(1 + d % 10), "synthetic")).ToList();
    private int _tick;
    public string Name => "synthetic";
    public int Priority => 0;
    public ProviderStatus Status => ProviderStatus.Ready();
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Update()
    {
        _tick++;
        _readings.Clear();
        for (var d = 0; d < devices; d++)
            for (var s = 0; s < perDevice; s++)
            {
                var t = Types[s % Types.Length];
                _readings.Add(new RawSensorReading($"dev{d}/s{s}", $"dev{d}", s % 8 == 0 ? $"Core #{s}" : $"{t} {s}", t, SensorUnitExtensions.DefaultUnitFor(t), 40 + (s + _tick) % 20, "synthetic", Index: s));
            }
    }
    public IReadOnlyList<RawSensorReading> ReadSensors() => _readings;
    public IReadOnlyList<HardwareDevice> GetDevices() => _devices;
    public void Dispose() { }
}
