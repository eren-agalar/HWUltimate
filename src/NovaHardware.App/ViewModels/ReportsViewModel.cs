using System.Collections.ObjectModel;
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

public sealed partial class ReportSectionOption : ObservableObject
{
    public ReportSectionOption(ReportSections section, bool included)
    {
        Section = section;
        _isIncluded = included;
    }

    public ReportSections Section { get; }
    public string Label => Loc.Get($"ReportSection_{Section}");
    [ObservableProperty] private bool _isIncluded;
}

/// <summary>Reports: section selection, live native preview of exactly what will be exported, validated export.</summary>
public sealed partial class ReportsViewModel : ObservableObject
{
    private readonly IReportExportService _export;
    private readonly IHardwareInventoryService _inventory;
    private readonly ISensorPollingService _polling;
    private readonly AppPaths _paths;

    public ReportsViewModel(IReportExportService export, IHardwareInventoryService inventory, ISensorPollingService polling, AppPaths paths)
    {
        _export = export;
        _inventory = inventory;
        _polling = polling;
        _paths = paths;
        foreach (var s in new[] { ReportSections.Summary, ReportSections.Inventory, ReportSections.Cpu, ReportSections.Gpu, ReportSections.Memory, ReportSections.Storage, ReportSections.Sensors })
        {
            var o = new ReportSectionOption(s, true);
            o.PropertyChanged += (_, _) => RefreshPreview();
            Sections.Add(o);
        }
    }

    public ObservableCollection<ReportSectionOption> Sections { get; } = [];
    public ObservableCollection<PreviewSection> Preview { get; } = [];
    public IReadOnlyList<ReportFormat> Formats => _export.SupportedFormats;

    [ObservableProperty] private ReportFormat _format = ReportFormat.Html;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _lastExport;
    [ObservableProperty] private bool _previewEmpty;

    private ReportData Build() => new()
    {
        ApplicationVersion = Bootstrapper.Version,
        Sections = Sections.Where(s => s.IsIncluded).Aggregate(ReportSections.None, (a, s) => a | s.Section),
        Inventory = _inventory.Current,
        Sensors = _polling.Current,
    };

    /// <summary>Rebuilds the preview from current data. Loads the inventory first if it has not been collected yet.</summary>
    public async void RefreshPreview()
    {
        if (_inventory.Current.CollectedAt == DateTimeOffset.MinValue)
        {
            IsBusy = true;
            try { await Task.Run(() => _inventory.RefreshAsync()); }
            catch (Exception ex) { Message = ex.Message; IsError = true; }
            finally { IsBusy = false; }
        }

        Preview.Clear();
        foreach (var section in ReportExportService.BuildPreview(Build())) Preview.Add(section);
        PreviewEmpty = Preview.Count == 0;
    }

    [RelayCommand]
    private void Refresh() => RefreshPreview();

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
            await _export.ExportAsync(Build(), Format, dialog.FileName); // writes, reads back and validates
            LastExport = dialog.FileName;
            Message = Loc.Format("Reports_Saved", dialog.FileName);
            IsError = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Message = Loc.Format("Reports_Failed", ex.Message);
            IsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLast()
    {
        if (LastExport is not null) Ui.Shell(LastExport);
    }

    [RelayCommand]
    private void OpenFolder() => Ui.OpenFolder(_paths.ReportDirectory);
}
