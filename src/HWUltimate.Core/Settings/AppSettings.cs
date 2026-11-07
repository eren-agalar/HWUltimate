using System.Text.Json;
using HWUltimate.Core.Appearance;
using HWUltimate.Core.Explorer;

namespace HWUltimate.Core.Settings;

public sealed record GeneralSettings
{
    /// <summary>IETF culture name, e.g. "en-US" or "tr-TR". Empty = follow Windows.</summary>
    public string Language { get; init; } = string.Empty;
    public bool LaunchWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool VerboseLogging { get; init; }
    public bool EnableNvidiaProvider { get; init; } = true;
    public bool EnableAmdProvider { get; init; } = true;
    /// <summary>Check GitHub for new releases at start-up and every few hours.</summary>
    public bool CheckForUpdates { get; init; } = true;
    /// <summary>A version the user chose to skip; no notification for it (a newer one notifies again).</summary>
    public string? SkippedUpdateVersion { get; init; }
}

public sealed record SensorSettings
{
    /// <summary>Central polling interval of the single sensor loop.</summary>
    public int PollingIntervalMs { get; init; } = 1000;
    public ExplorerDisplayMode DefaultDisplayMode { get; init; } = ExplorerDisplayMode.Compact;

    public static IReadOnlyList<int> PollingIntervals { get; } = [250, 500, 1000, 2000, 5000];
}

public sealed record AppSettings
{
    public const int CurrentSchema = 2;
    public int SchemaVersion { get; init; } = CurrentSchema;
    public GeneralSettings General { get; init; } = new();
    public AppearanceOptions Appearance { get; init; } = new();
    public SensorSettings Sensors { get; init; } = new();
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Validates settings before they are saved. Returns human-readable problems; empty means valid.</summary>
public static class SettingsValidator
{
    public static IReadOnlyList<string> Validate(AppSettings s)
    {
        var errors = new List<string>();
        if (!SensorSettings.PollingIntervals.Contains(s.Sensors.PollingIntervalMs)) errors.Add("Polling interval must be one of 250, 500, 1000, 2000 or 5000 ms.");
        if (!Enum.IsDefined(s.Appearance.Theme)) errors.Add("Unknown theme.");
        foreach (var (name, hex) in new[] { ("Accent", s.Appearance.AccentColor), ("Normal status", s.Appearance.NormalColor), ("Warning status", s.Appearance.WarningColor), ("Critical status", s.Appearance.CriticalColor) })
        {
            if (hex is not null && !Rgb.TryParse(hex, out _)) errors.Add($"{name} colour must be #RRGGBB.");
        }

        return errors;
    }
}

/// <summary>Reads v0.1 (flat) settings files into the v0.2 structure so user preferences survive the upgrade.</summary>
public static class SettingsMigration
{
    /// <summary>
    /// Theme names written before the rename to HWUltimate ("NovaDark", "NovaLight", "NovaMidnight", "NovaOcean")
    /// map to the current names so a saved theme survives the upgrade.
    /// </summary>
    public static string NormalizeLegacyNames(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "(\"Theme\"\\s*:\\s*\")Nova(Dark|Light|Midnight|Ocean)\"", "$1$2\"");

    public static bool IsLegacy(JsonElement root) => root.ValueKind == JsonValueKind.Object && !root.TryGetProperty(nameof(AppSettings.SchemaVersion), out _);

    public static AppSettings FromV1(JsonElement root)
    {
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Bool(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        int? Int(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

        var interval = Int("PollingIntervalMs") ?? 1000;
        return new AppSettings
        {
            General = new GeneralSettings
            {
                Language = Str("Language") ?? string.Empty,
                EnableNvidiaProvider = Bool("EnableNvidiaProvider") ?? true,
                EnableAmdProvider = Bool("EnableAmdProvider") ?? true,
            },
            Appearance = new AppearanceOptions { Theme = Str("Theme") == "Light" ? ThemeChoice.Light : ThemeChoice.Dark },
            Sensors = new SensorSettings { PollingIntervalMs = SensorSettings.PollingIntervals.Contains(interval) ? interval : 1000 },
        };
    }
}
