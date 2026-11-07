using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using HWUltimate.App.Services;
using HWUltimate.Core.Abstractions;
using HWUltimate.Core.HardwareInfo;
using HWUltimate.Domain.Hardware;
using HWUltimate.Domain.Sensors;
using HWUltimate.Infrastructure;
using HWUltimate.UI.Controls;
using HWUltimate.UI.Localization;

namespace HWUltimate.App.ViewModels;

public sealed record PropertyRow(HardwareProperty Property)
{
    public string Name => Property.Name;
    public string Value => Property.Status switch
    {
        PropertyStatus.Available => Property.Value,
        PropertyStatus.RequiresElevation => Loc.Get("Hw_RequiresAdmin"),
        _ => Loc.Get("Hw_NotAvailable"),
    };
    public string? Source => Property.Source;
    public string? Note => Property.Note;
    public bool IsAvailable => Property.IsAvailable;
    public bool HasNote => !string.IsNullOrEmpty(Property.Note);

    /// <summary>Official manufacturer specification (catalog), detected from the system, or unavailable.</summary>
    public string OriginKey => !Property.IsAvailable ? "Unavailable"
        : Property.Source == GpuChipCatalog.SourceLabel ? "Official"
        : "Detected";

    public string Origin => Loc.Get($"Origin_{OriginKey}");
}

public sealed record PropertySection(string Key, string Title, IReadOnlyList<PropertyRow> Rows);

/// <summary>One line of the detail table: a section header band or a property (Feature | Description).</summary>
public sealed record DetailRow(string? Header, PropertyRow? Property, LiveSensorRow? Live = null)
{
    public bool IsHeader => Header is not null;
    public bool IsProperty => Property is not null;
    public bool IsLive => Live is not null;
}

public sealed partial class DeviceItem : ObservableObject
{
    public DeviceItem(DeviceInfo device)
    {
        Device = device;
        Icon = IconMap.ForCategory(device.Category);
        CategoryTitle = device.Category == HardwareCategory.System ? Environment.MachineName : Loc.Get($"Category_{device.Category}");
        Available = device.Properties.Count(p => p.IsAvailable);
        Unavailable = device.Properties.Count - Available;
    }

    public DeviceInfo Device { get; }
    public string Name => Device.Name;
    public string Icon { get; }
    public string CategoryTitle { get; }
    public int CategoryOrder => (int)Device.Category;
    public int Available { get; }
    public int Unavailable { get; }
    public string Counts => Loc.Format("Hw_PropertyCount", Available, Unavailable);

    [ObservableProperty] private int _matchCount;
}

public sealed record LiveSensorRow(string Group, string Name, string Value, string Min, string Max);

/// <summary>Hardware identification center: device list, sectioned properties, live sensors, search, copy and export.</summary>
public sealed partial class HardwareViewModel : ObservableObject
{
    private readonly IHardwareInventoryService _inventory;
    private readonly ISensorPollingService _polling;
    private readonly AppPaths _paths;
    private HardwareInventory _current = HardwareInventory.Empty;

    public HardwareViewModel(IHardwareInventoryService inventory, ISensorPollingService polling, AppPaths paths)
    {
        _inventory = inventory;
        _polling = polling;
        _paths = paths;
        DeviceView = CollectionViewSource.GetDefaultView(Devices);
        DeviceView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceItem.CategoryTitle)));
        DeviceView.SortDescriptions.Add(new SortDescription(nameof(DeviceItem.CategoryOrder), ListSortDirection.Ascending));
        DeviceView.Filter = o => o is DeviceItem d && (string.IsNullOrWhiteSpace(SearchText) || d.MatchCount > 0);
    }

    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public ICollectionView DeviceView { get; }
    public ObservableCollection<PropertySection> Sections { get; } = [];
    public RangeObservableCollection<DetailRow> DetailRows { get; } = [];
    public ObservableCollection<LiveSensorRow> LiveSensors { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public IReadOnlyList<HardwareInfoFormat> ExportFormats { get; } = Enum.GetValues<HardwareInfoFormat>();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _collected = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _searchSummary = string.Empty;
    [ObservableProperty] private DeviceItem? _selectedDevice;
    [ObservableProperty] private PropertySection? _selectedSection;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _hasLiveSensors;

    public async Task LoadAsync()
    {
        if (_inventory.Current.CollectedAt == DateTimeOffset.MinValue) await RefreshAsync();
        else Show(_inventory.Current);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Show(await Task.Run(() => _inventory.RefreshAsync()));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(HardwareInventory inv)
    {
        _current = inv;
        var selectedId = SelectedDevice?.Device.Id;
        Devices.Clear();
        Devices.Add(new DeviceItem(SystemSummaryBuilder.Build(inv, Loc.Get("Hw_SystemSummary"))));
        foreach (var d in inv.Devices) Devices.Add(new DeviceItem(d));
        Warnings.Clear();
        foreach (var w in inv.Warnings) Warnings.Add(w);
        Collected = Loc.Format("Hw_Collected", inv.CollectedAt.LocalDateTime);
        ApplySearch();
        SelectedDevice = Devices.FirstOrDefault(d => d.Device.Id == selectedId) ?? Devices.FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        var q = SearchText.Trim();
        var all = new HardwareInventory(_current.CollectedAt, _current.Summary, Devices.Select(d => d.Device).ToList(), []);
        var matches = HardwareInfoExporter.Search(all, q);
        var byDevice = matches.GroupBy(m => m.Device.Id).ToDictionary(g => g.Key, g => g.Count());
        foreach (var d in Devices) d.MatchCount = byDevice.GetValueOrDefault(d.Device.Id);
        DeviceView.Refresh();
        SearchSummary = q.Length == 0 ? string.Empty : Loc.Format("Hw_Matches", matches.Count, byDevice.Count);
        BuildSections();
        if (q.Length > 0 && SelectedDevice is { MatchCount: 0 })
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.MatchCount > 0);
        }
    }

    partial void OnSelectedDeviceChanged(DeviceItem? value)
    {
        BuildSections();
        RefreshLiveSensors();
    }

    private void BuildSections()
    {
        var previous = SelectedSection?.Key;
        Sections.Clear();
        if (SelectedDevice is null) return;
        var q = SearchText.Trim();
        foreach (var (key, props) in HardwareInfoExporter.Sections(SelectedDevice.Device))
        {
            var rows = props.Where(p => q.Length == 0
                    || SelectedDevice.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || p.Value.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || (p.Source?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false))
                .Select(p => new PropertyRow(p)).ToList();
            if (rows.Count > 0) Sections.Add(new PropertySection(key, SectionTitle(key), rows));
        }

        SelectedSection = Sections.FirstOrDefault(s => s.Key == previous) ?? Sections.FirstOrDefault();
        BuildDetail();
    }

    private void BuildDetail()
    {
        var rows = new List<DetailRow>();
        foreach (var section in Sections)
        {
            rows.Add(new DetailRow(section.Title, null));
            rows.AddRange(section.Rows.Select(r => new DetailRow(null, r)));
        }

        if (LiveSensors.Count > 0)
        {
            rows.Add(new DetailRow(Loc.Get("Hw_LiveSensors"), null));
            rows.AddRange(LiveSensors.Select(l => new DetailRow(null, null, l)));
        }

        DetailRows.ReplaceAll(rows);
    }

    public static string SectionTitle(string key) =>
        Loc.Get("Section_" + new string(key.Where(char.IsLetter).ToArray())) is var t && !t.StartsWith("Section_", StringComparison.Ordinal) ? t : key;

    /// <summary>Real sensor values for the selected device, matched to the sensor provider's device by category and name.</summary>
    [RelayCommand]
    private void RefreshLiveSensors()
    {
        LiveSensors.Clear();
        if (SelectedDevice?.Device is not { } d)
        {
            HasLiveSensors = false;
            return;
        }

        var snap = _polling.Current;
        var name = Normalize(d.Name);
        var matches = snap.Devices.Where(x => x.Category == d.Category && (Normalize(x.Name) == name || Normalize(x.Name).Contains(name, StringComparison.Ordinal) || name.Contains(Normalize(x.Name), StringComparison.Ordinal)))
            .Select(x => x.DeviceId).ToHashSet(StringComparer.Ordinal);
        var c = Loc.Instance.Culture;
        foreach (var s in snap.Sensors.Where(s => matches.Contains(s.DeviceId) && s.IsMeasurement && s.SampleCount > 0))
        {
            LiveSensors.Add(new LiveSensorRow(Loc.Get($"SensorType_{s.Type}"), s.Name, s.Unit.FormatValue(s.Value, c, Loc.Get("Common_NA")),
                s.Unit.FormatValue(s.Minimum, c, "—"), s.Unit.FormatValue(s.Maximum, c, "—")));
        }

        HasLiveSensors = LiveSensors.Count > 0;
        BuildDetail();
    }

    private static string Normalize(string s) => string.Join(' ', s.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [RelayCommand]
    private void CopyProperty(PropertyRow? row)
    {
        if (row is null) return;
        Notify(ClipboardService.SetText(HardwareInfoExporter.FormatProperty(row.Property)));
    }

    [RelayCommand]
    private void CopyValue(PropertyRow? row)
    {
        if (row is null) return;
        Notify(ClipboardService.SetText(row.Value));
    }

    [RelayCommand]
    private void CopyDevice()
    {
        if (SelectedDevice is null) return;
        Notify(ClipboardService.SetText(HardwareInfoExporter.FormatDevice(SelectedDevice.Device)));
    }

    [RelayCommand]
    private void CopyAll() =>
        Notify(ClipboardService.SetText(HardwareInfoExporter.Export(Inventory(), HardwareInfoFormat.Text, Bootstrapper.Version)));

    [RelayCommand]
    private async Task ExportAsync(HardwareInfoFormat format)
    {
        var ext = HardwareInfoExporter.FileExtension(format);
        var dialog = new SaveFileDialog
        {
            InitialDirectory = _paths.ReportDirectory,
            FileName = $"HWUltimate-Info-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}{ext}",
            Filter = $"{format} (*{ext})|*{ext}",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, HardwareInfoExporter.Export(Inventory(), format, Bootstrapper.Version));
            Message = Loc.Format("Hw_Exported", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
    }

    private HardwareInventory Inventory() =>
        new(_current.CollectedAt, _current.Summary, Devices.Select(d => d.Device).ToList(), _current.Warnings);

    private void Notify(bool ok) => Message = ok ? Loc.Get("Hw_Copied") : string.Empty;
}
