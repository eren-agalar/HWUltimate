using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Alerts;
using NovaHardware.Core.Settings;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Sensors;
using NovaHardware.Hardware;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

public sealed partial class NavPage : ObservableObject
{
    public NavPage(string key, string icon, ObservableObject viewModel)
    {
        Key = key;
        Icon = icon;
        ViewModel = viewModel;
        Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Key { get; }
    public string Icon { get; }
    public ObservableObject ViewModel { get; }
    public string Title => Loc.Get(Key);

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _badge;
}

public sealed record ProviderStatusItem(string Name, bool IsAvailable, string Message);

public sealed record Toast(AlertSeverity Severity, string Title, string Message, DateTimeOffset Time);

/// <summary>Shell: navigation, the snapshot fan-out to visible consumers, provider status and alert toasts.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISensorPollingService _polling;
    private readonly IHardwareInventoryService _inventory;
    private readonly IAlertService _alerts;
    private readonly ISettingsService _settings;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SensorExplorerViewModel _sensors;
    private readonly OverlayViewModel _overlay;
    private readonly NavPage _alertsPage;
    private int _uiUpdatePending;

    public MainViewModel(
        ISensorPollingService polling, IHardwareInventoryService inventory, IAlertService alerts, ISettingsService settings, ILogger<MainViewModel> logger,
        SensorExplorerViewModel sensors, HardwareViewModel hardware, OverlayViewModel overlay, AlertsViewModel alertsVm, ReportsViewModel reports, SettingsViewModel settingsVm)
    {
        _polling = polling;
        _inventory = inventory;
        _alerts = alerts;
        _settings = settings;
        _logger = logger;
        _sensors = sensors;
        _overlay = overlay;
        _alertsPage = new NavPage("Nav_Alerts", "Bell", alertsVm);
        Pages =
        [
            new("Nav_Sensors", "Thermometer", sensors),
            new("Nav_Hardware", "Cpu", hardware),
            new("Nav_Overlay", "Overlay", overlay),
            _alertsPage,
            new("Nav_Reports", "Report", reports),
            new("Nav_Settings", "Settings", settingsVm),
        ];
        _selectedPage = Pages[0];
        Pages[0].IsSelected = true;
        IsElevated = Elevation.IsAdministrator();
        _alerts.AlertRaised += OnAlert;
    }

    /// <summary>The only navigable pages. Removed v0.1 pages (Dashboard, History, Stability) have no route.</summary>
    public IReadOnlyList<NavPage> Pages { get; }

    public ObservableCollection<ProviderStatusItem> Providers { get; } = [];
    public ObservableCollection<Toast> Toasts { get; } = [];
    public string Version => Bootstrapper.Version;
    public bool IsElevated { get; }

    [ObservableProperty] private NavPage _selectedPage;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _statusText = string.Empty;

    partial void OnSelectedPageChanged(NavPage value)
    {
        _sensors.IsActive = value.ViewModel is SensorExplorerViewModel;
        _overlay.Controller.PreviewActive = value.ViewModel is OverlayViewModel;
        switch (value.ViewModel)
        {
            case HardwareViewModel h: _ = h.LoadAsync(); break;
            case AlertsViewModel a: a.Load(); break;
            case ReportsViewModel r: r.RefreshPreview(); break;
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            var s = _settings.Current;
            _alerts.SetComponentRules(s.Alerts.ComponentRules);
            _alerts.SetRules(s.Alerts.CustomRules);

            await _polling.InitializeAsync();
            _polling.SnapshotAvailable += OnSnapshot;
            // One poll off the UI thread so the first screen and the inventory (Super I/O, EC names) have real data.
            await Task.Run(_polling.PollOnce);
            _polling.Start();
            _sensors.IsActive = SelectedPage.ViewModel is SensorExplorerViewModel;
            RefreshProviders();

            _ = Task.Run(async () =>
            {
                try { await _inventory.RefreshAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "Inventory refresh failed"); }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialisation failed");
            StatusText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnSnapshot(object? sender, SensorSnapshot snapshot)
    {
        // Runs on the polling thread: alert evaluation here, UI work coalesced on the dispatcher.
        _alerts.Evaluate(snapshot);
        _sensors.Update(snapshot);
        if (Interlocked.Exchange(ref _uiUpdatePending, 1) == 1) return;
        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _uiUpdatePending, 0);
            var latest = _polling.Current;
            StatusText = Loc.Format("Status_Poll", latest.PollDuration.TotalMilliseconds, latest.Sensors.Count, latest.Devices.Count);
            _alertsPage.Badge = _alerts.ActiveAlertCount;
            if (Providers.Count == 0 || latest.Timestamp.Second % 10 == 0) RefreshProviders();
        });
    }

    private void RefreshProviders()
    {
        Providers.Clear();
        foreach (var (name, status) in _polling.ProviderStatuses) Providers.Add(new ProviderStatusItem(name, status.IsAvailable, status.Message));
    }

    private void OnAlert(object? sender, AlertEvent e)
    {
        if (e.State != AlertState.Raised || !_settings.Current.Alerts.NotificationsEnabled) return;
        Ui.Post(() =>
        {
            var toast = new Toast(e.Severity, e.RuleName, e.Message, e.Timestamp);
            Toasts.Insert(0, toast);
            while (Toasts.Count > 3) Toasts.RemoveAt(Toasts.Count - 1);
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Ui.Post(() => Toasts.Remove(toast)), TaskScheduler.Default);
        });
    }

    [RelayCommand]
    private void DismissToast(Toast toast) => Toasts.Remove(toast);

    [RelayCommand]
    private static void RestartElevated() => Ui.RestartElevated();

    [RelayCommand]
    private void Navigate(string key)
    {
        var page = Pages.FirstOrDefault(p => p.Key == key);
        if (page is null)
        {
            _logger.LogDebug("Ignored navigation to unknown page {Key}", key);
            return;
        }

        foreach (var p in Pages) p.IsSelected = ReferenceEquals(p, page);
        SelectedPage = page;
    }
}
