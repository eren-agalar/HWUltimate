using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Appearance;
using NovaHardware.Core.Overlay;
using NovaHardware.Core.Settings;
using NovaHardware.Domain.Hardware;
using NovaHardware.Infrastructure.Overlay;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

public sealed partial class OverlayMetricOption : ObservableObject
{
    public OverlayMetricOption(OverlayMetric metric, bool enabled)
    {
        Metric = metric;
        _isEnabled = enabled;
    }

    public OverlayMetric Metric { get; }
    public string Label => Loc.Get($"Overlay_Metric_{Metric}");
    public bool NeedsFrameSource => Metric is OverlayMetric.Fps or OverlayMetric.FrameTime;
    [ObservableProperty] private bool _isEnabled;
}

public sealed record ColorSwatch(string Name, string Hex);

/// <summary>Mini Overlay page: live preview, presets, metric selection / order, appearance, behaviour and FPS source status.</summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ISensorPollingService _polling;
    private bool _loading;

    public OverlayViewModel(OverlayController controller, ISettingsService settings, ISensorPollingService polling)
    {
        Controller = controller;
        _settings = settings;
        _polling = polling;
        Load(settings.Current.Overlay);
    }

    public OverlayController Controller { get; }
    public ObservableCollection<OverlayMetricOption> Metrics { get; } = [];
    public ObservableCollection<FilterOption<string>> Gpus { get; } = [];
    public IReadOnlyList<OverlayPreset> Presets { get; } = Enum.GetValues<OverlayPreset>();
    public IReadOnlyList<int> RefreshIntervals => OverlaySettings.RefreshIntervals;
    public IReadOnlyList<ColorSwatch> Swatches { get; } =
    [
        new("Nova Blue", "#6595FF"), new("White", "#F3F5F8"), new("Mint", "#36C991"), new("Amber", "#E9AC4B"), new("Rose", "#F2738C"), new("Violet", "#A78BFA"),
    ];

    [ObservableProperty] private OverlayPreset _preset;
    [ObservableProperty] private double _backgroundOpacity;
    [ObservableProperty] private double _scale;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private string _textColor = "#F3F5F8";
    [ObservableProperty] private string _accentColor = "#6595FF";
    [ObservableProperty] private string _backgroundColor = "#11151D";
    [ObservableProperty] private bool _showGraphs;
    [ObservableProperty] private bool _showLabels;
    [ObservableProperty] private bool _isHorizontal;
    [ObservableProperty] private int _refreshIntervalMs;
    [ObservableProperty] private bool _clickThrough;
    [ObservableProperty] private string _hotkey = string.Empty;
    [ObservableProperty] private bool _showOnStartup;
    [ObservableProperty] private bool _autoHideWhenNovaActive;
    [ObservableProperty] private bool _autoHideWithoutGame;
    [ObservableProperty] private FilterOption<string>? _gpu;
    [ObservableProperty] private string? _presentMonPath;
    [ObservableProperty] private string _validationMessage = string.Empty;
    [ObservableProperty] private string _presentMonStatus = string.Empty;

    private void Load(OverlaySettings s)
    {
        _loading = true;
        Preset = s.Preset;
        BackgroundOpacity = s.BackgroundOpacity;
        Scale = s.Scale;
        FontSize = s.FontSize;
        TextColor = s.TextColor;
        AccentColor = s.AccentColor;
        BackgroundColor = s.BackgroundColor;
        ShowGraphs = s.ShowGraphs;
        ShowLabels = s.ShowLabels;
        IsHorizontal = s.Orientation == OverlayOrientation.Horizontal;
        RefreshIntervalMs = s.RefreshIntervalMs;
        ClickThrough = s.ClickThrough;
        Hotkey = s.Hotkey;
        ShowOnStartup = s.ShowOnStartup;
        AutoHideWhenNovaActive = s.AutoHideWhenNovaActive;
        AutoHideWithoutGame = s.AutoHideWithoutGame;
        PresentMonPath = s.PresentMonPath;
        Metrics.Clear();
        foreach (var m in s.Metrics) Metrics.Add(Option(m, true));
        foreach (var m in Enum.GetValues<OverlayMetric>().Except(s.Metrics)) Metrics.Add(Option(m, false));
        RefreshGpus(s.GpuDeviceId);
        UpdatePresentMonStatus();
        _loading = false;
    }

    private OverlayMetricOption Option(OverlayMetric m, bool enabled)
    {
        var o = new OverlayMetricOption(m, enabled);
        o.PropertyChanged += (_, _) => { if (!_loading) { Preset = OverlayPreset.Custom; Commit(); } };
        return o;
    }

    public void RefreshGpus(string? selected = null)
    {
        selected ??= Gpu?.Value;
        Gpus.Clear();
        Gpus.Add(new FilterOption<string>(null, Loc.Get("Overlay_GpuAuto")));
        foreach (var g in _polling.Current.DevicesOf(HardwareCategory.Gpu)) Gpus.Add(new FilterOption<string>(g.DeviceId, g.Name));
        Gpu = Gpus.FirstOrDefault(g => g.Value == selected) ?? Gpus[0];
    }

    private void UpdatePresentMonStatus()
    {
        var path = PresentMonFrameSource.Locate(PresentMonPath);
        PresentMonStatus = path is null ? Loc.Get("Overlay_PresentMonMissing") : Loc.Format("Overlay_PresentMonFound", path);
    }

    private OverlaySettings Build() => _settings.Current.Overlay with
    {
        Preset = Preset,
        Metrics = Metrics.Where(m => m.IsEnabled).Select(m => m.Metric).ToList(),
        BackgroundOpacity = Math.Round(BackgroundOpacity, 2),
        Scale = Math.Round(Scale, 2),
        FontSize = Math.Round(FontSize),
        TextColor = TextColor,
        AccentColor = AccentColor,
        BackgroundColor = BackgroundColor,
        ShowGraphs = ShowGraphs,
        ShowLabels = ShowLabels,
        Orientation = IsHorizontal ? OverlayOrientation.Horizontal : OverlayOrientation.Vertical,
        RefreshIntervalMs = RefreshIntervalMs,
        ClickThrough = ClickThrough,
        Hotkey = Hotkey,
        ShowOnStartup = ShowOnStartup,
        AutoHideWhenNovaActive = AutoHideWhenNovaActive,
        AutoHideWithoutGame = AutoHideWithoutGame,
        GpuDeviceId = Gpu?.Value,
        PresentMonPath = string.IsNullOrWhiteSpace(PresentMonPath) ? null : PresentMonPath,
        Left = Controller.Settings.Left,
        Top = Controller.Settings.Top,
    };

    /// <summary>Validates, persists and applies the overlay settings immediately.</summary>
    private async void Commit()
    {
        if (_loading) return;
        var updated = _settings.Current with { Overlay = Build() };
        var errors = SettingsValidator.Validate(updated);
        ValidationMessage = string.Join("  ", errors);
        if (errors.Count > 0) return;
        Controller.Apply(updated.Overlay);
        try
        {
            await _settings.SaveAsync(updated);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ValidationMessage = ex.Message;
        }
    }

    partial void OnPresetChanged(OverlayPreset value)
    {
        if (_loading || value == OverlayPreset.Custom) { Commit(); return; }
        _loading = true;
        var chosen = OverlayPresets.For(value);
        Metrics.Clear();
        foreach (var m in chosen) Metrics.Add(Option(m, true));
        foreach (var m in Enum.GetValues<OverlayMetric>().Except(chosen)) Metrics.Add(Option(m, false));
        _loading = false;
        Commit();
    }

    partial void OnBackgroundOpacityChanged(double value) => Commit();
    partial void OnScaleChanged(double value) => Commit();
    partial void OnFontSizeChanged(double value) => Commit();
    partial void OnTextColorChanged(string value) => Commit();
    partial void OnAccentColorChanged(string value) => Commit();
    partial void OnBackgroundColorChanged(string value) => Commit();
    partial void OnShowGraphsChanged(bool value) => Commit();
    partial void OnShowLabelsChanged(bool value) => Commit();
    partial void OnIsHorizontalChanged(bool value) => Commit();
    partial void OnRefreshIntervalMsChanged(int value) => Commit();
    partial void OnClickThroughChanged(bool value) => Commit();
    partial void OnHotkeyChanged(string value) => Commit();
    partial void OnShowOnStartupChanged(bool value) => Commit();
    partial void OnAutoHideWhenNovaActiveChanged(bool value) => Commit();
    partial void OnAutoHideWithoutGameChanged(bool value) => Commit();
    partial void OnGpuChanged(FilterOption<string>? value) => Commit();
    partial void OnPresentMonPathChanged(string? value) { UpdatePresentMonStatus(); Commit(); }

    [RelayCommand]
    private void ToggleOverlay() => Controller.Toggle();

    [RelayCommand]
    private void ResetPosition() => Controller.ResetPosition();

    [RelayCommand]
    private void MoveUp(OverlayMetricOption? o) => Move(o, -1);

    [RelayCommand]
    private void MoveDown(OverlayMetricOption? o) => Move(o, 1);

    private void Move(OverlayMetricOption? o, int delta)
    {
        if (o is null) return;
        var i = Metrics.IndexOf(o);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Metrics.Count) return;
        Metrics.Move(i, j);
        Preset = OverlayPreset.Custom;
        Commit();
    }

    [RelayCommand]
    private void SetTextColor(string hex) => TextColor = hex;

    [RelayCommand]
    private void SetAccentColor(string hex) => AccentColor = hex;

    [RelayCommand]
    private void BrowsePresentMon()
    {
        var dialog = new OpenFileDialog { Filter = "PresentMon (PresentMon*.exe)|PresentMon*.exe|Executable (*.exe)|*.exe" };
        if (dialog.ShowDialog() == true) PresentMonPath = dialog.FileName;
    }

    [RelayCommand]
    private static void OpenPresentMonPage() => Ui.Shell("https://github.com/GameTechDev/PresentMon/releases");

    public static bool IsValidColor(string hex) => Rgb.TryParse(hex, out _);
}
