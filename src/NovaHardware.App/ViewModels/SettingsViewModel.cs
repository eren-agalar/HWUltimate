using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.History;
using NovaHardware.Core.Settings;
using NovaHardware.Infrastructure;
using NovaHardware.UI.Localization;
using NovaHardware.UI.Themes;

namespace NovaHardware.App.ViewModels;

public sealed record LanguageOption(string Culture, string Name);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ISensorPollingService _polling;
    private readonly HistoryRecorder _recorder;
    private readonly AppPaths _paths;

    public SettingsViewModel(ISettingsService settings, ISensorPollingService polling, HistoryRecorder recorder, AppPaths paths)
    {
        _settings = settings;
        _polling = polling;
        _recorder = recorder;
        _paths = paths;
        Languages = Loc.Languages.Select(l => new LanguageOption(l.Culture, l.DisplayName)).ToList();
        Load();
    }

    public IReadOnlyList<LanguageOption> Languages { get; }
    public IReadOnlyList<int> Intervals { get; } = [250, 500, 1000, 2000, 5000];
    public string DataFolder => _paths.Root;
    public string Version => Bootstrapper.Version;

    [ObservableProperty] private int _pollingIntervalMs;
    [ObservableProperty] private bool _historyEnabled;
    [ObservableProperty] private int _sampleIntervalSeconds;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private LanguageOption? _language;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private bool _enableNvidia;
    [ObservableProperty] private bool _enableAmd;
    [ObservableProperty] private double _thermalLimit;
    [ObservableProperty] private string _message = string.Empty;

    private void Load()
    {
        var s = _settings.Current;
        PollingIntervalMs = s.PollingIntervalMs;
        HistoryEnabled = s.HistoryEnabled;
        SampleIntervalSeconds = s.HistorySampleIntervalSeconds;
        RetentionDays = s.HistoryRetentionDays;
        Language = Languages.FirstOrDefault(l => l.Culture == s.Language) ?? Languages[0];
        IsDark = s.Theme == ThemeMode.Dark;
        EnableNvidia = s.EnableNvidiaProvider;
        EnableAmd = s.EnableAmdProvider;
        ThermalLimit = s.StressThermalLimitCelsius;
    }

    // Language and theme apply live so the user sees the effect immediately.
    partial void OnLanguageChanged(LanguageOption? value)
    {
        if (value is not null) Loc.Instance.Apply(value.Culture);
    }

    partial void OnIsDarkChanged(bool value) => ThemeManager.Apply(value ? AppTheme.Dark : AppTheme.Light);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var updated = _settings.Current with
        {
            PollingIntervalMs = Math.Clamp(PollingIntervalMs, 250, 60_000),
            HistoryEnabled = HistoryEnabled,
            HistorySampleIntervalSeconds = Math.Clamp(SampleIntervalSeconds, 1, 3600),
            HistoryRetentionDays = Math.Clamp(RetentionDays, 1, 3650),
            Language = Language?.Culture ?? string.Empty,
            Theme = IsDark ? ThemeMode.Dark : ThemeMode.Light,
            EnableNvidiaProvider = EnableNvidia,
            EnableAmdProvider = EnableAmd,
            StressThermalLimitCelsius = Math.Clamp(ThermalLimit, 50, 110),
        };
        await _settings.SaveAsync(updated);

        _polling.Interval = TimeSpan.FromMilliseconds(updated.PollingIntervalMs);
        _recorder.Enabled = updated.HistoryEnabled;
        _recorder.SampleInterval = TimeSpan.FromSeconds(updated.HistorySampleIntervalSeconds);
        _recorder.Retention = TimeSpan.FromDays(updated.HistoryRetentionDays);
        Message = Loc.Get("Settings_Saved");
    }

    [RelayCommand]
    private void OpenDataFolder() => Ui.OpenFolder(_paths.Root);
}
