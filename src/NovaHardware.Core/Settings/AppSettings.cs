using NovaHardware.Domain.Alerts;

namespace NovaHardware.Core.Settings;

public enum ThemeMode { Dark = 0, Light }

public sealed record AppSettings
{
    public int PollingIntervalMs { get; init; } = 1000;
    public bool HistoryEnabled { get; init; } = true;
    public int HistorySampleIntervalSeconds { get; init; } = 2;
    public int HistoryRetentionDays { get; init; } = 14;
    /// <summary>IETF culture name, e.g. "en-US" or "tr-TR". Empty = follow Windows.</summary>
    public string Language { get; init; } = string.Empty;
    public ThemeMode Theme { get; init; } = ThemeMode.Dark;
    public bool EnableNvidiaProvider { get; init; } = true;
    public bool EnableAmdProvider { get; init; } = true;
    public bool MinimizeToTray { get; init; }
    public double StressThermalLimitCelsius { get; init; } = 95;
    public List<AlertRule>? AlertRules { get; init; }
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
