using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Reports;
using NovaHardware.Domain.Reports;
using NovaHardware.Infrastructure;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

public sealed partial class ReportsViewModel : ObservableObject
{
    private readonly IReportExportService _export;
    private readonly IHardwareInventoryService _inventory;
    private readonly ISensorPollingService _polling;
    private readonly IAlertService _alerts;
    private readonly IStressTestService _stress;
    private readonly AppPaths _paths;

    public ReportsViewModel(IReportExportService export, IHardwareInventoryService inventory, ISensorPollingService polling,
        IAlertService alerts, IStressTestService stress, AppPaths paths)
    {
        _export = export;
        _inventory = inventory;
        _polling = polling;
        _alerts = alerts;
        _stress = stress;
        _paths = paths;
    }

    public IReadOnlyList<ReportFormat> Formats => _export.SupportedFormats;

    [ObservableProperty] private ReportFormat _format = ReportFormat.Html;
    [ObservableProperty] private bool _includeSummary = true;
    [ObservableProperty] private bool _includeDevices = true;
    [ObservableProperty] private bool _includeSensors = true;
    [ObservableProperty] private bool _includeAlerts = true;
    [ObservableProperty] private bool _includeStress = true;
    [ObservableProperty] private string _preview = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isBusy;

    private async Task<ReportData> BuildAsync()
    {
        var inventory = _inventory.Current.CollectedAt == DateTimeOffset.MinValue ? await _inventory.RefreshAsync() : _inventory.Current;
        var sections = ReportSections.None;
        if (IncludeSummary) sections |= ReportSections.Summary;
        if (IncludeDevices) sections |= ReportSections.Devices;
        if (IncludeSensors) sections |= ReportSections.Sensors;
        if (IncludeAlerts) sections |= ReportSections.Alerts;
        if (IncludeStress) sections |= ReportSections.StressTests;
        return new ReportData
        {
            ApplicationVersion = Bootstrapper.Version,
            Sections = sections,
            Inventory = inventory,
            Sensors = _polling.Current,
            Alerts = _alerts.RecentEvents,
            StressTests = _stress.Results,
        };
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        IsBusy = true;
        try
        {
            var data = await BuildAsync();
            // Preview always uses the plain-text renderer so it is readable inside the app.
            Preview = _export.Render(data, ReportFormat.Text);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var ext = ReportExportService.FileExtension(Format);
        var dialog = new SaveFileDialog
        {
            InitialDirectory = _paths.ReportDirectory,
            FileName = $"NovaHardware-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}{ext}",
            Filter = $"{Format} (*{ext})|*{ext}",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            await _export.ExportAsync(await BuildAsync(), Format, dialog.FileName);
            Message = Loc.Format("Reports_Saved", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder() => Ui.OpenFolder(_paths.ReportDirectory);
}
