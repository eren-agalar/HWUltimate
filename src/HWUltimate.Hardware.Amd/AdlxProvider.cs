using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Hardware.Amd;

/// <summary>
/// AMD ADLX integration point. This build detects and initialises the ADLX runtime and reports its version.
/// GPU metric binding (IADLXPerformanceMonitoringServices) is intentionally not wired yet: calling ADLX
/// interfaces by v-table offset without the official headers risks crashing the process, so AMD GPU sensors are
/// currently supplied by the LibreHardwareMonitor provider. No values are fabricated here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdlxProvider : IHardwareProvider
{
    public const string ProviderName = "ADLX";

    private readonly ILogger<AdlxProvider> _logger;
    private AdlxNative? _adlx;

    public AdlxProvider(ILogger<AdlxProvider> logger)
    {
        _logger = logger;
    }

    public string Name => ProviderName;
    public int Priority => 20;
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;
    public string? RuntimeVersion => _adlx?.Version;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var adlx = AdlxNative.TryLoad(out var reason);
            if (adlx is null)
            {
                Status = ProviderStatus.Unavailable(reason);
                return Task.CompletedTask;
            }

            var result = adlx.Initialize();
            if (result != AdlxNative.Ok)
            {
                adlx.Dispose();
                Status = ProviderStatus.Unavailable($"ADLXInitialize returned {result} (driver may be too old or no AMD GPU active).");
                return Task.CompletedTask;
            }

            _adlx = adlx;
            // Reported as not available for polling: there are no sensors to read from this provider yet.
            Status = ProviderStatus.Unavailable($"ADLX {adlx.Version ?? "?"} runtime detected; AMD GPU telemetry is provided through LibreHardwareMonitor in this build.");
            _logger.LogInformation("ADLX runtime {Version} initialised", adlx.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ADLX initialisation failed");
            Status = ProviderStatus.Unavailable($"ADLX error: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public void Update()
    {
    }

    public IReadOnlyList<RawSensorReading> ReadSensors() => [];

    public IReadOnlyList<HardwareDevice> GetDevices() => [];

    public void Dispose()
    {
        _adlx?.Dispose();
        _adlx = null;
    }
}
