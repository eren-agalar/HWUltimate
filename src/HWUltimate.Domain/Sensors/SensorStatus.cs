namespace HWUltimate.Domain.Sensors;

/// <summary>Whether a sensor can currently be read.</summary>
public enum SensorAvailability
{
    Available = 0,
    /// <summary>Sensor exists but the provider returned no value for this poll.</summary>
    NoData,
    /// <summary>The sensor requires administrator rights / a kernel driver that is not loaded.</summary>
    RequiresElevation,
    /// <summary>Hardware or driver does not expose this sensor.</summary>
    NotSupported,
    /// <summary>The provider failed while reading.</summary>
    Error,
}

/// <summary>Confidence in a reported value.</summary>
public enum DataQuality
{
    /// <summary>Fresh value inside the plausible physical range for its type.</summary>
    Good = 0,
    /// <summary>No new value has arrived for longer than the staleness window; the last real value is shown.</summary>
    Stale,
    /// <summary>The value is outside the plausible range for its type. It is preserved unchanged but flagged.</summary>
    Suspect,
    /// <summary>No value is available. The UI must show "N/A" rather than a number.</summary>
    Missing,
}
