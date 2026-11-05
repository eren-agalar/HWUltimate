using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.History;
using NovaHardware.Core.Settings;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Sensors;
using NovaHardware.Hardware;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

public sealed partial class NavPage : ObservableObject
{
    public NavPage(string key, string glyph, ObservableObject viewModel)
    {
        Key = key;
        Glyph = glyph;
        ViewModel = viewModel;
        Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Key { get; }
    public string Glyph { get; }
    public ObservableObject ViewModel { get; }
    public string Title => Loc.Get(Key);

    [ObservableProperty] private bool _isSelected;
}

public sealed record ProviderStatusItem(string Name, bool IsAvailable, string Message);

public sealed record Toast(AlertSeverity Severity, string Title, string Message, DateTimeOffset Time);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISensorPollingService _polling;
    private readonly IHardwareInventoryService _inventory;
    private readonly IHistoricalDataService _history;
    private readonly HistoryRecorder _recorder;
    private readonly IAlertService _alerts;
    private readonly ISettingsService _settings;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DashboardViewModel _dashboard;
    private readonly SensorExplorerViewModel _sensors;
    private readonly StabilityViewModel _stability;
    private int _uiUpdatePending;

    public MainViewModel(
        ISensorPollingService polling, IHardwareInventoryService inventory, IHistoricalDataService history, HistoryRecorder recorder,
        IAlertService alerts, ISettingsService settings, ILogger<MainViewModel> logger,
        DashboardViewModel dashboard, SensorExplorerViewModel sensors, HardwareViewModel hardware, HistoryViewModel historyVm,
        StabilityViewModel stability, AlertsViewModel alertsVm, ReportsViewModel reports, SettingsViewModel settingsVm)
    {
        _polling = polling;
        _inventory = inventory;
        _history = history;
        _recorder = recorder;
        _alerts = alerts;
        _settings = settings;
        _logger = logger;
        _dashboard = dashboard;
        _sensors = sensors;
        _stability = stability;

        Pages =
        [
            new("Nav_Dashboard", "Dashboard", dashboard),
            new("Nav_Sensors", "Thermometer", sensors),
            new("Nav_Hardware", "Cpu", hardware),
            new("Nav_History", "History", historyVm),
            new("Nav_Stability", "Health", stability),
            new("Nav_Alerts", "Bell", alertsVm),
            new("Nav_Reports", "Report", reports),
            new("Nav_Settings", "Settings", settingsVm),
        ];
        _selectedPage = Pages[0];
        Pages[0].IsSelected = true;
        _sensors.IsActive = false;
        IsElevated = Elevation.IsAdministrator();
        _alerts.AlertRaised += OnAlert;
    }

    public IReadOnlyList<NavPage> Pages { get; }

    public ObservableCollection<ProviderStatusItem> Providers { get; } = [];

    public ObservableCollection<Toast> Toasts { get; } = [];

    public string Version => Bootstrapper.Version;

    public bool IsElevated { get; }

    [ObservableProperty]
    private NavPage _selectedPage;

    /// <summary>Lazily loads page data when a page is first shown.</summary>
    partial void OnSelectedPageChanged(NavPage value)
    {
        _sensors.IsActive = value.ViewModel is SensorExplorerViewModel;
        switch (value.ViewModel)
        {
            case HardwareViewModel h: _ = h.LoadAsync(); break;
            case HistoryViewModel h: _ = h.LoadAsync(); break;
            case AlertsViewModel a: a.Load(); break;
        }
    }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            var settings = _settings.Current;
            _alerts.SetRules(settings.AlertRules is { Count: > 0 } rules ? rules : Core.Alerts.AlertService.DefaultRules);

            await _history.InitializeAsync();
            _recorder.Enabled = settings.HistoryEnabled;
            _recorder.SampleInterval = TimeSpan.FromSeconds(Math.Max(1, settings.HistorySampleIntervalSeconds));
            _recorder.Retention = TimeSpan.FromDays(Math.Max(1, settings.HistoryRetentionDays));

            await _polling.InitializeAsync();
            _polling.SnapshotAvailable += OnSnapshot;
            // One synchronous poll so the first screen and the inventory (Super I/O, EC names) have real data.
            await Task.Run(_polling.PollOnce);
            _recorder.Start();
            _polling.Start();
            RefreshProviders();

            _ = Task.Run(async () =>
            {
                try
                {
                    await _inventory.RefreshAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inventory refresh failed");
                }
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
        // Alert evaluation runs on the polling thread; UI updates are coalesced so a slow frame never queues up work.
        _alerts.Evaluate(snapshot);
        if (Interlocked.Exchange(ref _uiUpdatePending, 1) == 1)
        {
            return;
        }

        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _uiUpdatePending, 0);
            var latest = _polling.Current;
            _dashboard.Update(latest);
            _sensors.Update(latest);
            _stability.Update(latest);
            StatusText = Loc.Format("Dash_Poll", latest.PollDuration.TotalMilliseconds, latest.Sensors.Count);
            if (Providers.Count == 0 || latest.Timestamp.Second % 10 == 0)
            {
                RefreshProviders();
            }
        });
    }

    private void RefreshProviders()
    {
        var statuses = _polling.ProviderStatuses;
        Providers.Clear();
        foreach (var (name, status) in statuses)
        {
            Providers.Add(new ProviderStatusItem(name, status.IsAvailable, status.Message));
        }

        _dashboard.Providers = Providers;
    }

    private void OnAlert(object? sender, AlertEvent e)
    {
        if (e.State != AlertState.Raised)
        {
            return;
        }

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
        if (Pages.FirstOrDefault(p => p.Key == key) is { } page)
        {
            foreach (var p in Pages) p.IsSelected = ReferenceEquals(p, page);
            SelectedPage = page;
        }
    }
}
