using System.IO;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using NovaHardware.App.Views;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Overlay;
using NovaHardware.Core.Settings;
using NovaHardware.Infrastructure.Overlay;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.Services;

/// <summary>One rendered overlay line (updated in place to avoid allocations and re-layout).</summary>
public sealed partial class OverlayItem : ObservableObject
{
    public OverlayItem(OverlayMetric metric) => Metric = metric;

    public OverlayMetric Metric { get; }
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _text = "N/A";
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private IReadOnlyList<double?>? _samples;
    [ObservableProperty] private int _version;
}

/// <summary>
/// Owns the overlay window, its refresh timer, the global show/hide shortcut and click-through mode.
/// All values come from <see cref="OverlayEngine"/>, which reads the shared sensor snapshot — no extra hardware polling.
/// </summary>
public sealed partial class OverlayController : ObservableObject, IDisposable
{
    private const int HotkeyId = 0x4E4F; // "NO"
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly ISettingsService _settings;
    private readonly ILogger<OverlayController> _logger;
    private readonly OverlayEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _positionSave;
    private OverlayWindow? _window;
    private HwndSource? _hotkeyHost;
    private bool _hotkeyRegistered;
    private bool _disposed;

    public OverlayController(ISensorPollingService polling, IFrameSource frames, ISettingsService settings, ILogger<OverlayController> logger)
    {
        _settings = settings;
        _logger = logger;
        _engine = new OverlayEngine(polling, frames, ForegroundProcess.CurrentId);
        _engine.Apply(settings.Current.Overlay);
        _engine.VisibilityChanged += (_, v) => { IsVisible = v; };
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(settings.Current.Overlay.RefreshIntervalMs) };
        _timer.Tick += (_, _) => Refresh();
        _positionSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _positionSave.Tick += async (_, _) =>
        {
            _positionSave.Stop();
            await PersistAsync(_settings.Current with { Overlay = _settings.Current.Overlay with { Left = _engine.Settings.Left, Top = _engine.Settings.Top } });
        };
        _engine.SettingsChanged += (_, _) => { _positionSave.Stop(); _positionSave.Start(); };
        BuildItems();
    }

    public ObservableCollection<OverlayItem> Items { get; } = [];

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _previewActive;
    [ObservableProperty] private string _hotkeyStatus = string.Empty;
    [ObservableProperty] private string _frameStatus = string.Empty;

    public OverlaySettings Settings => _engine.Settings;

    /// <summary>Called once the main window has a handle: registers the global shortcut.</summary>
    public void Attach(Window owner)
    {
        var helper = new WindowInteropHelper(owner);
        _hotkeyHost = HwndSource.FromHwnd(helper.EnsureHandle());
        _hotkeyHost?.AddHook(WndProc);
        RegisterHotkey();
        if (_settings.Current.Overlay.ShowOnStartup) Show();
    }

    public void Apply(OverlaySettings settings)
    {
        var hotkeyChanged = settings.Hotkey != _engine.Settings.Hotkey;
        _engine.Apply(settings);
        _timer.Interval = TimeSpan.FromMilliseconds(settings.RefreshIntervalMs);
        BuildItems();
        _window?.ApplySettings(settings);
        if (hotkeyChanged) RegisterHotkey();
        Refresh();
    }

    private void BuildItems()
    {
        var existing = Items.ToDictionary(i => i.Metric);
        Items.Clear();
        foreach (var line in _engine.Lines) Items.Add(existing.GetValueOrDefault(line.Metric) ?? new OverlayItem(line.Metric));
    }

    public void Show()
    {
        if (_disposed) return;
        if (_window is null)
        {
            _window = new OverlayWindow(this);
            _window.ApplySettings(_engine.Settings);
            _window.LocationChanged += (_, _) => { if (_window is { } w) _engine.UpdatePosition(w.Left, w.Top); };
        }

        _engine.Show();
        _window.Show();
        _window.ApplyClickThrough(_engine.Settings.ClickThrough);
        Refresh();
        _timer.Start();
    }

    public void Hide()
    {
        if (!PreviewActive) _timer.Stop();
        _engine.Hide();
        _window?.Hide();
        FrameStatus = string.Empty;
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    public void ResetPosition()
    {
        var area = SystemParameters.WorkArea;
        _window?.MoveTo(area.Left + 24, area.Top + 24);
        _engine.UpdatePosition(area.Left + 24, area.Top + 24);
    }

    /// <summary>The Mini Overlay page shows a live preview while it is open, even when the overlay itself is hidden.</summary>
    partial void OnPreviewActiveChanged(bool value)
    {
        if (value) { Refresh(); _timer.Start(); }
        else if (!IsVisible) _timer.Stop();
    }

    private void Refresh()
    {
        if (!IsVisible && !PreviewActive) return;
        var lines = _engine.Tick(Loc.Instance.Culture, preview: !IsVisible);
        for (var i = 0; i < lines.Count && i < Items.Count; i++)
        {
            var v = lines[i].Value;
            var item = Items[i];
            item.Label = Loc.Get($"Overlay_Metric_{lines[i].Metric}");
            item.Text = v.Text;
            item.Detail = v.Detail;
            item.IsAvailable = v.IsAvailable;
            if (_engine.Settings.ShowGraphs)
            {
                item.Samples = lines[i].History();
                item.Version++;
            }
        }

        FrameStatus = IsVisible && OverlayPresets.NeedsFrameTelemetry(_engine.Settings.Metrics) ? _engine.LastFps.Message : string.Empty;
        var novaActive = Application.Current.MainWindow?.IsActive == true;
        _window?.SetAutoHidden(_engine.ShouldAutoHide(novaActive));
    }

    // ------------------------------------------------------------------ global shortcut
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    private void RegisterHotkey()
    {
        if (_hotkeyHost is null) return;
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hotkeyHost.Handle, HotkeyId);
            _hotkeyRegistered = false;
        }

        if (!HotkeyGesture.TryParse(_engine.Settings.Hotkey, out var gesture, out var error))
        {
            HotkeyStatus = error ?? string.Empty;
            return;
        }

        _hotkeyRegistered = RegisterHotKey(_hotkeyHost.Handle, HotkeyId, gesture.Modifiers | ModNoRepeat, gesture.VirtualKey);
        HotkeyStatus = _hotkeyRegistered
            ? Loc.Format("Overlay_HotkeyActive", gesture)
            : Loc.Format("Overlay_HotkeyInUse", gesture);
        if (!_hotkeyRegistered) _logger.LogWarning("Overlay shortcut {Hotkey} could not be registered ({Error})", gesture, Marshal.GetLastPInvokeError());
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == HotkeyId)
        {
            Toggle();
            handled = true;
        }

        return 0;
    }

    private async Task PersistAsync(AppSettings settings)
    {
        if (SettingsValidator.Validate(settings).Count > 0) return;
        try
        {
            await _settings.SaveAsync(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save overlay position");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _positionSave.Stop();
        if (_hotkeyRegistered && _hotkeyHost is not null) UnregisterHotKey(_hotkeyHost.Handle, HotkeyId);
        _hotkeyHost?.RemoveHook(WndProc);
        _engine.Dispose(); // stops PresentMon
        _window?.Close();
        _window = null;
    }
}
