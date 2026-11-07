using System.Text.RegularExpressions;
using HWUltimate.Domain.Sensors;

namespace HWUltimate.Core.Identity;

/// <summary>
/// Separates live measurements from configuration / threshold values that some providers expose through the same
/// sensor API (e.g. NVMe "Warning Composite Temperature", "Thermal Sensor High Limit", "Temperature Sensor Resolution").
/// Metadata is kept and shown in the advanced view, but never presented as a live reading.
/// </summary>
public static partial class SensorKindClassifier
{
    [GeneratedRegex(@"\b(limit|threshold|resolution|trip|critical|warning|shutdown|slowdown|tj\s*max|tcase|max(imum)?\s+operating|target)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TemperatureMetadata();

    [GeneratedRegex(@"\bdistance\b|\bto\s+tj\s*max\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistanceToLimit();

    [GeneratedRegex(@"\b(resolution|threshold|trip point)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericMetadata();

    public static SensorKind Classify(SensorType type, string name)
    {
        if (type == SensorType.Temperature)
        {
            // "Core #1 Distance to TjMax" is a real, changing measurement.
            if (DistanceToLimit().IsMatch(name)) return SensorKind.Measurement;
            return TemperatureMetadata().IsMatch(name) ? SensorKind.Metadata : SensorKind.Measurement;
        }

        return GenericMetadata().IsMatch(name) ? SensorKind.Metadata : SensorKind.Measurement;
    }
}
