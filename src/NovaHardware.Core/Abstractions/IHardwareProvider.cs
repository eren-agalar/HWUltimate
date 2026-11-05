using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Abstractions;

/// <summary>Current health of a provider, surfaced in the UI so users know why sensors may be missing.</summary>
public sealed record ProviderStatus(bool IsAvailable, string Message, bool RequiresElevation = false)
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

/// <summary>A hardware back-end (LibreHardwareMonitor, NVAPI, ADLX …) isolated behind an interface.</summary>
public interface IHardwareProvider : ISensorProvider, IDisposable
{
    /// <summary>Lower numbers are polled first and win when two providers report the same sensor id.</summary>
    int Priority { get; }

    ProviderStatus Status { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<HardwareDevice> GetDevices();
}
