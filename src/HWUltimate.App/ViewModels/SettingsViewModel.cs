using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HWUltimate.App.Services;
using HWUltimate.Core.Abstractions;
using HWUltimate.Core.Appearance;
using HWUltimate.Core.Settings;
using HWUltimate.Infrastructure;
using HWUltimate.UI.Localization;
using HWUltimate.UI.Themes;

namespace HWUltimate.App.ViewModels;

public sealed record LanguageOption(string Culture, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A theme card with its preview colours.</summary>
public sealed record ThemeOption(ThemeChoice Choice, string Name, string Background, string Panel, string Accent, string Text, string Muted)
{
    public static ThemeOption From(ThemeChoice choice, bool systemLight)
    {
        var p = ThemeCatalog.Base(choice, systemLight);
        return new ThemeOption(choice, Loc.Get($"Theme_{choice}"), p.Background.Hex, p.Panel.Hex, p.Accent.Hex, p.Text.Hex, p.TextMuted.Hex);
    }
}

public sealed record SettingsSection(string Key, string Icon)
{
    public string Title => Loc.Get($"SettingsSection_{Key}");
}

/// <summary>A named accent colour shown as a swatch.</summary>
public sealed record ColorSwatch(string Name, string Hex);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ISensorPollingService _polling;
    private readonly AppPaths _paths;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, ISensorPollingService polling, AppPaths paths,
        SensorExplorerViewModel explorer)
    {
        _settings = settings;
        _polling = polling;
        _paths = paths;
        Explorer = explorer;
        Languages = Loc.Languages.Select(l => new LanguageOption(l.Culture, l.DisplayName)).ToList();
        Sections = [new("Appearance", "Palette"), new("Sensors", "Thermometer"), new("General", "Settings")];
        _selectedSection = Sections[0];
        Load();
        ThemeManager.Changed += (_, _) => RefreshThemeCards();
    }

    public SensorExplorerViewModel Explorer { get; }
    public IReadOnlyList<SettingsSection> Sections { get; }
    public IReadOnlyList<LanguageOption> Languages { get; }
    public ObservableCollection<ThemeOption> Themes { get; } = [];
    public ObservableCollection<ColorSwatch> AccentPresets { get; } = [];
    public ObservableCollection<ProviderStatusItem> Providers { get; } = [];
    public IReadOnlyList<int> PollingIntervals => SensorSettings.PollingIntervals;
    public string DataFolder => _paths.Root;
    public string Version => Bootstrapper.Version;

    [ObservableProperty] private SettingsSection _selectedSection;
    [ObservableProperty] private ThemeChoice _theme;
    [ObservableProperty] private string _accentColor = string.Empty;
    [ObservableProperty] private string _normalColor = string.Empty;
    [ObservableProperty] private string _warningColor = string.Empty;
    [ObservableProperty] private string _criticalColor = string.Empty;
    [ObservableProperty] private bool _animationsEnabled;
    [ObservableProperty] private bool _isComfortable;
    [ObservableProperty] private TypographyScale _typography;
    [ObservableProperty] private int _pollingIntervalMs;
    [ObservableProperty] private LanguageOption? _language;
    [ObservableProperty] private bool _launchWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _verboseLogging;
    [ObservableProperty] private bool _enableNvidia;
    [ObservableProperty] private bool _enableAmd;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _restartRequired;

    private void Load()
    {
        _loading = true;
        var s = _settings.Current;
        Theme = s.Appearance.Theme;
        AccentColor = s.Appearance.AccentColor ?? string.Empty;
        NormalColor = s.Appearance.NormalColor ?? string.Empty;
        WarningColor = s.Appearance.WarningColor ?? string.Empty;
        CriticalColor = s.Appearance.CriticalColor ?? string.Empty;
        AnimationsEnabled = s.Appearance.AnimationsEnabled;
        IsComfortable = s.Appearance.Density == InterfaceDensity.Comfortable;
        Typography = s.Appearance.Typography;
        PollingIntervalMs = s.Sensors.PollingIntervalMs;
        Language = Languages.FirstOrDefault(l => l.Culture == s.General.Language) ?? Languages[0];
        LaunchWithWindows = OperatingSystem.IsWindows() && StartupRegistration.IsEnabled();
        StartMinimized = s.General.StartMinimized;
        VerboseLogging = s.General.VerboseLogging;
        EnableNvidia = s.General.EnableNvidiaProvider;
        EnableAmd = s.General.EnableAmdProvider;
        RefreshThemeCards();
        _loading = false;
    }

    private void RefreshThemeCards()
    {
        var systemLight = SystemTheme.UsesLightTheme();
        Themes.Clear();
        foreach (var c in Enum.GetValues<ThemeChoice>()) Themes.Add(ThemeOption.From(c, systemLight));
        AccentPresets.Clear();
        var light = ThemeManager.Palette.IsLight;
        foreach (var (name, dark, lightHex) in ThemeCatalog.AccentPresets) AccentPresets.Add(new ColorSwatch(name, light ? lightHex : dark));
    }

    public void RefreshProviders()
    {
        Providers.Clear();
        foreach (var (name, status) in _polling.ProviderStatuses) Providers.Add(new ProviderStatusItem(name, status.IsAvailable, status.Message));
    }

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        if (value.Key == "General") RefreshProviders();
    }

    private static string? Hex(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private AppearanceOptions Appearance() => new()
    {
        Theme = Theme,
        AccentColor = Hex(AccentColor),
        NormalColor = Hex(NormalColor),
        WarningColor = Hex(WarningColor),
        CriticalColor = Hex(CriticalColor),
        AnimationsEnabled = AnimationsEnabled,
        Density = IsComfortable ? InterfaceDensity.Comfortable : InterfaceDensity.Compact,
        Typography = Typography,
    };

    /// <summary>Validates, applies and saves. Appearance changes repaint the whole app immediately.</summary>
    private async void Commit(Func<AppSettings, AppSettings> change, bool requiresRestart = false)
    {
        if (_loading) return;
        var updated = change(_settings.Current);
        var errors = SettingsValidator.Validate(updated);
        if (errors.Count > 0)
        {
            Message = string.Join("\n", errors);
            IsError = true;
            return;
        }

        ThemeManager.Apply(updated.Appearance);
        _polling.Interval = TimeSpan.FromMilliseconds(updated.Sensors.PollingIntervalMs);
        try
        {
            await _settings.SaveAsync(updated);
            IsError = false;
            RestartRequired |= requiresRestart;
            Message = RestartRequired ? Loc.Get("Settings_RestartRequired") : Loc.Get("Settings_Saved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
            IsError = true;
        }
    }

    private void CommitAppearance() => Commit(s => s with { Appearance = Appearance() });

    partial void OnThemeChanged(ThemeChoice value) => CommitAppearance();
    partial void OnAccentColorChanged(string value) { if (string.IsNullOrWhiteSpace(value) || Rgb.TryParse(value, out _)) CommitAppearance(); }
    partial void OnNormalColorChanged(string value) { if (string.IsNullOrWhiteSpace(value) || Rgb.TryParse(value, out _)) CommitAppearance(); }
    partial void OnWarningColorChanged(string value) { if (string.IsNullOrWhiteSpace(value) || Rgb.TryParse(value, out _)) CommitAppearance(); }
    partial void OnCriticalColorChanged(string value) { if (string.IsNullOrWhiteSpace(value) || Rgb.TryParse(value, out _)) CommitAppearance(); }
    partial void OnAnimationsEnabledChanged(bool value) => CommitAppearance();
    partial void OnIsComfortableChanged(bool value) => CommitAppearance();
    partial void OnTypographyChanged(TypographyScale value) => CommitAppearance();

    partial void OnPollingIntervalMsChanged(int value) => Commit(s => s with { Sensors = s.Sensors with { PollingIntervalMs = value } });

    partial void OnLanguageChanged(LanguageOption? value)
    {
        if (value is null || _loading) return;
        Loc.Instance.Apply(value.Culture);
        Commit(s => s with { General = s.General with { Language = value.Culture } });
    }

    partial void OnLaunchWithWindowsChanged(bool value)
    {
        if (_loading || !OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return;
        StartupRegistration.Set(value, exe, StartMinimized);
        Commit(s => s with { General = s.General with { LaunchWithWindows = value } });
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_loading && LaunchWithWindows && OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe) StartupRegistration.Set(true, exe, value);
        Commit(s => s with { General = s.General with { StartMinimized = value } });
    }

    partial void OnVerboseLoggingChanged(bool value) => Commit(s => s with { General = s.General with { VerboseLogging = value } }, requiresRestart: true);
    partial void OnEnableNvidiaChanged(bool value) => Commit(s => s with { General = s.General with { EnableNvidiaProvider = value } }, requiresRestart: true);
    partial void OnEnableAmdChanged(bool value) => Commit(s => s with { General = s.General with { EnableAmdProvider = value } }, requiresRestart: true);

    [RelayCommand]
    private void SelectTheme(ThemeChoice choice) => Theme = choice;

    [RelayCommand]
    private void SetAccent(string hex) => AccentColor = hex;

    [RelayCommand]
    private void ResetAccent() => AccentColor = string.Empty;

    [RelayCommand]
    private void ResetStatusColors()
    {
        _loading = true;
        NormalColor = WarningColor = CriticalColor = string.Empty;
        _loading = false;
        CommitAppearance();
    }

    [RelayCommand]
    private void SetTypography(string value) => Typography = Enum.Parse<TypographyScale>(value);

    [RelayCommand]
    private void OpenDataFolder() => Ui.OpenFolder(_paths.Root);

    [RelayCommand]
    private void OpenLogs() => Ui.OpenFolder(_paths.LogDirectory);

    [RelayCommand]
    private static void RestartApp()
    {
        if (Environment.ProcessPath is not { } exe) return;
        // The new instance may start before this one releases the single-instance mutex.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--allow-multiple") { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }
}
