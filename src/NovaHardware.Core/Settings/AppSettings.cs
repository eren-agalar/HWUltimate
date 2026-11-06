using System.Text.Json;
using NovaHardware.Core.Appearance;
using NovaHardware.Core.Explorer;
using NovaHardware.Core.Overlay;
using NovaHardware.Domain.Alerts;

namespace NovaHardware.Core.Settings;

public sealed record GeneralSettings
{
    /// <summary>IETF culture name, e.g. "en-US" or "tr-TR". Empty = follow Windows.</summary>
    public string Language { get; init; } = string.Empty;
    public bool LaunchWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool VerboseLogging { get; init; }
    public bool EnableNvidiaProvider { get; init; } = true;
    public bool EnableAmdProvider { get; init; } = true;
}

public sealed record SensorSettings
{
    /// <summary>Central polling interval of the single sensor loop.</summary>
    public int PollingIntervalMs { get; init; } = 1000;
    public ExplorerDisplayMode DefaultDisplayMode { get; init; } = ExplorerDisplayMode.Compact;

    public static IReadOnlyList<int> PollingIntervals { get; } = [250, 500, 1000, 2000, 5000];
}

public sealed record AlertSettings
{
    public bool NotificationsEnabled { get; init; } = true;
    public List<ComponentAlertRule> ComponentRules { get; init; } = [];
    public List<AlertRule> CustomRules { get; init; } = [];
}

public sealed record AppSettings
{
    public const int CurrentSchema = 2;
    public int SchemaVersion { get; init; } = CurrentSchema;
    public GeneralSettings General { get; init; } = new();
    public AppearanceOptions Appearance { get; init; } = new();
    public SensorSettings Sensors { get; init; } = new();
    public OverlaySettings Overlay { get; init; } = new();
    public AlertSettings Alerts { get; init; } = new();
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

        var o = s.Overlay;
        if (o.BackgroundOpacity is < 0 or > 1) errors.Add("Overlay background opacity must be between 0 and 100 %.");
        if (o.Scale is < 0.5 or > 3) errors.Add("Overlay scale must be between 50 % and 300 %.");
        if (o.FontSize is < 8 or > 40) errors.Add("Overlay font size must be between 8 and 40.");
        if (!OverlaySettings.RefreshIntervals.Contains(o.RefreshIntervalMs)) errors.Add("Overlay refresh must be 250, 500, 1000 or 2000 ms.");
        foreach (var (name, hex) in new[] { ("Overlay text", o.TextColor), ("Overlay accent", o.AccentColor), ("Overlay background", o.BackgroundColor) })
        {
            if (!Rgb.TryParse(hex, out _)) errors.Add($"{name} colour must be #RRGGBB.");
        }

        if (!HotkeyGesture.TryParse(o.Hotkey, out _, out var hk)) errors.Add($"Overlay shortcut: {hk}");
        if (o.Metrics.Count == 0) errors.Add("Select at least one overlay metric.");
        if (o.Metrics.Count != o.Metrics.Distinct().Count()) errors.Add("Each overlay metric can appear only once.");
        foreach (var rule in s.Alerts.ComponentRules) errors.AddRange(Alerts.AlertRuleValidator.Validate(rule).Select(e => $"{Alerts.AlertService.RuleName(rule.Role)}: {e}"));
        foreach (var rule in s.Alerts.CustomRules) errors.AddRange(Alerts.AlertRuleValidator.Validate(rule).Select(e => $"{rule.Name}: {e}"));
        return errors;
    }
}

/// <summary>Reads v0.1 (flat) settings files into the v0.2 structure so user preferences survive the upgrade.</summary>
public static class SettingsMigration
{
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
            Appearance = new AppearanceOptions { Theme = Str("Theme") == "Light" ? ThemeChoice.NovaLight : ThemeChoice.NovaDark },
            Sensors = new SensorSettings { PollingIntervalMs = SensorSettings.PollingIntervals.Contains(interval) ? interval : 1000 },
            // v0.1 single-threshold alert rules are not carried over: v0.2 replaces them with component-aware rules.
        };
    }
}
