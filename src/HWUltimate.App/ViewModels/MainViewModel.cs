using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HWUltimate.App.Services;
using HWUltimate.Core.Abstractions;
using HWUltimate.Domain.Sensors;
using HWUltimate.Hardware;
using HWUltimate.UI.Localization;

namespace HWUltimate.App.ViewModels;

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
}

public sealed record ProviderStatusItem(string Name, bool IsAvailable, string Message);

/// <summary>Shell: navigation, the snapshot fan-out to visible consumers and provider status.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISensorPollingService _polling;
    private readonly IHardwareInventoryService _inventory;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SensorExplorerViewModel _sensors;
    private int _uiUpdatePending;

    public MainViewModel(
        ISensorPollingService polling, IHardwareInventoryService inventory, ILogger<MainViewModel> logger,
        SensorExplorerViewModel sensors, HardwareViewModel hardware, ReportsViewModel reports, SettingsViewModel settingsVm, UpdateViewModel updates)
    {
        Updates = updates;
        _polling = polling;
        _inventory = inventory;
        _logger = logger;
        _sensors = sensors;
        Pages =
        [
            new("Nav_Sensors", "Thermometer", sensors),
            new("Nav_Hardware", "Cpu", hardware),
            new("Nav_Reports", "Report", reports),
            new("Nav_Settings", "Settings", settingsVm),
        ];
        _selectedPage = Pages[0];
        Pages[0].IsSelected = true;
        IsElevated = Elevation.IsAdministrator();
    }

    /// <summary>The only navigable pages. Removed pages have no route.</summary>
    public IReadOnlyList<NavPage> Pages { get; }

    public ObservableCollection<ProviderStatusItem> Providers { get; } = [];
    public UpdateViewModel Updates { get; }
    public string Version => Bootstrapper.Version;
    public bool IsElevated { get; }

    [ObservableProperty] private NavPage _selectedPage;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _statusText = string.Empty;

    partial void OnSelectedPageChanged(NavPage value)
    {
        _sensors.IsActive = value.ViewModel is SensorExplorerViewModel;
        switch (value.ViewModel)
        {
            case HardwareViewModel h: _ = h.LoadAsync(); break;
            case ReportsViewModel r: r.RefreshPreview(); break;
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
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
        // Runs on the polling thread; UI work is coalesced on the dispatcher.
        _sensors.Update(snapshot);
        if (Interlocked.Exchange(ref _uiUpdatePending, 1) == 1) return;
        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _uiUpdatePending, 0);
            var latest = _polling.Current;
            StatusText = Loc.Format("Status_Poll", latest.PollDuration.TotalMilliseconds, latest.Sensors.Count, latest.Devices.Count);
            if (Providers.Count == 0 || latest.Timestamp.Second % 10 == 0) RefreshProviders();
        });
    }

    private void RefreshProviders()
    {
        Providers.Clear();
        foreach (var (name, status) in _polling.ProviderStatuses) Providers.Add(new ProviderStatusItem(name, status.IsAvailable, status.Message));
    }

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
