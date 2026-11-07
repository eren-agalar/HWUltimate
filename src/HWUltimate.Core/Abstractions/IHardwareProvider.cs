using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Core.Abstractions;

/// <summary>Current health of a provider, surfaced in the UI so users know why sensors may be missing.</summary>
/// <param name="LowLevelDriverMissing">
/// True when the provider works but its low-level hardware driver (PawnIO for LibreHardwareMonitor) is not installed, so
/// motherboard, fan, voltage and some CPU sensors cannot be read until it is.
/// </param>
public sealed record ProviderStatus(bool IsAvailable, string Message, bool RequiresElevation = false, bool LowLevelDriverMissing = false)
{
    public static ProviderStatus NotInitialized { get; } = new(false, "Not initialized");
    public static ProviderStatus Ready(string message = "Ready") => new(true, message);
    public static ProviderStatus Unavailable(string message, bool requiresElevation = false) => new(false, message, requiresElevation);
}

/// <summary>Read surface of a sensor source.</summary>
public interface ISensorProvider
{
    string Name { get; }

    /// <summary>Refreshes the provider's internal state from hardware. Called once per polling cycle.</summary>
    void Update();

    /// <summary>
    /// Returns the readings captured by the most recent <see cref="Update"/>.
    /// Implementations must return <c>null</c> values rather than inventing data.
    /// </summary>
    IReadOnlyList<RawSensorReading> ReadSensors();
}

/// <summary>Outcome of a provider re-scan.</summary>
/// <param name="Changed">True when the provider now exposes a different (larger) set of devices or sensors.</param>
public sealed record RescanResult(bool Changed, string Message)
{
    public static RescanResult Unchanged(string message = "unchanged") => new(false, message);
}

/// <summary>
/// A provider whose hardware detection can be repeated at run time. Used when detection at start-up was incomplete
/// (driver not ready yet at logon, another monitoring tool holding the bus, a provider failing to open) so that a
/// restart of the application is never needed to see every sensor.
/// </summary>
public interface IRescannableProvider
{
    /// <summary>
    /// Repeats detection off the polling thread. Without <paramref name="force"/> the provider re-detects only when its
    /// current detection failed or looks incomplete; with it (user-requested) it always re-detects.
    /// </summary>
    Task<RescanResult> RescanAsync(bool force, CancellationToken cancellationToken = default);
}

/// <summary>A hardware back-end (LibreHardwareMonitor, NVAPI, ADLX …) isolated behind an interface.</summary>
public interface IHardwareProvider : ISensorProvider, IDisposable
{
    /// <summary>Lower numbers are polled first and win when two providers report the same sensor id.</summary>
    int Priority { get; }

    ProviderStatus Status { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<HardwareDevice> GetDevices();
}
