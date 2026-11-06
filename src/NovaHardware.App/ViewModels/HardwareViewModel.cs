using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaHardware.App.Services;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.HardwareInfo;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using NovaHardware.Infrastructure;
using NovaHardware.UI.Controls;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

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

public sealed partial class DeviceItem : ObservableObject
{
    public DeviceItem(DeviceInfo device)
    {
        Device = device;
        Icon = IconMap.ForCategory(device.Category);
        CategoryTitle = Loc.Get($"Category_{device.Category}");
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
        Devices.Add(new DeviceItem(SummaryDevice(inv.Summary)));
        foreach (var d in inv.Devices) Devices.Add(new DeviceItem(d));
        Warnings.Clear();
        foreach (var w in inv.Warnings) Warnings.Add(w);
        Collected = Loc.Format("Hw_Collected", inv.CollectedAt.LocalDateTime);
        ApplySearch();
        SelectedDevice = Devices.FirstOrDefault(d => d.Device.Id == selectedId) ?? Devices.FirstOrDefault();
    }

    /// <summary>The OS / system summary presented as a pseudo-device so it uses the same layout, copy and search.</summary>
    internal static DeviceInfo SummaryDevice(SystemSummary s)
    {
        var props = new List<HardwareProperty>();
        void Add(string name, string? value, string section = PropertySections.Overview)
        {
            props.Add(string.IsNullOrWhiteSpace(value)
                ? HardwareProperty.Unavailable(name, section, "Not reported by WMI.", "WMI")
                : new HardwareProperty(name, value, section, "WMI"));
        }

        Add("Computer", s.ComputerName);
        Add("Operating system", s.OperatingSystem);
        Add("OS version", s.OsVersion is null ? null : $"{s.OsVersion} (build {s.OsBuild})");
        Add("System", $"{s.SystemManufacturer} {s.SystemModel}".Trim());
        Add("Motherboard", s.Motherboard);
        Add("BIOS", $"{s.BiosVersion} {s.BiosDate}".Trim(), PropertySections.Firmware);
        Add("Processor", s.Processor);
        Add("Cores / threads", s.PhysicalCores is null ? null : $"{s.PhysicalCores} / {s.LogicalProcessors}", PropertySections.Specifications);
        Add("Installed memory", s.TotalMemoryBytes is { } b ? $"{b / 1024d / 1024 / 1024:0.#} GB" : null, PropertySections.Specifications);
        Add("Graphics", string.Join("; ", s.Gpus));
        Add("Uptime", s.Uptime is { } u ? $"{(int)u.TotalDays}d {u.Hours}h {u.Minutes}m" : null, PropertySections.Advanced);
        return new DeviceInfo { Id = "summary", Name = Loc.Get("Hw_SystemSummary"), Category = HardwareCategory.System, Source = "WMI", Properties = props };
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
        foreach (var s in snap.Sensors.Where(s => matches.Contains(s.DeviceId) && s.IsMeasurement))
        {
            LiveSensors.Add(new LiveSensorRow(Loc.Get($"SensorType_{s.Type}"), s.Name, s.Unit.FormatValue(s.Value, c, Loc.Get("Common_NA")),
                s.Unit.FormatValue(s.Minimum, c, "—"), s.Unit.FormatValue(s.Maximum, c, "—")));
        }

        HasLiveSensors = LiveSensors.Count > 0;
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
            FileName = $"NovaHardware-Info-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}{ext}",
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
