using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Hardware;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

public sealed record SummaryRow(string Label, string Value);

public sealed record DeviceGroup(HardwareCategory Category, string Glyph, IReadOnlyList<DeviceInfo> Devices);

public sealed partial class HardwareViewModel : ObservableObject
{
    private readonly IHardwareInventoryService _inventory;

    public HardwareViewModel(IHardwareInventoryService inventory)
    {
        _inventory = inventory;
    }

    public ObservableCollection<SummaryRow> Summary { get; } = [];
    public ObservableCollection<DeviceGroup> Groups { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _collected = string.Empty;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_inventory.Current.CollectedAt == DateTimeOffset.MinValue)
        {
            await RefreshAsync();
        }
        else
        {
            Show(_inventory.Current);
        }
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
        var s = inv.Summary;
        var na = Loc.Get("Common_NA");
        Summary.Clear();
        void Add(string key, string? value) => Summary.Add(new SummaryRow(Loc.Get(key), string.IsNullOrWhiteSpace(value) ? na : value));
        Add("Hw_Computer", s.ComputerName);
        Add("Hw_OS", s.OperatingSystem is null ? null : $"{s.OperatingSystem} ({s.OsVersion}, build {s.OsBuild})");
        Add("Hw_System", $"{s.SystemManufacturer} {s.SystemModel}".Trim());
        Add("Hw_Motherboard", s.Motherboard);
        Add("Hw_Bios", $"{s.BiosVersion} {s.BiosDate}".Trim());
        Add("Hw_Processor", s.Processor);
        Add("Hw_Cores", s.PhysicalCores is null ? null : $"{s.PhysicalCores} / {s.LogicalProcessors}");
        Add("Hw_Memory", s.TotalMemoryBytes is { } b ? $"{b / 1024d / 1024 / 1024:0.#} GB" : null);
        Add("Hw_Graphics", string.Join("; ", s.Gpus));
        Add("Hw_Uptime", s.Uptime is { } u ? $"{(int)u.TotalDays}d {u.Hours}h {u.Minutes}m" : null);

        Groups.Clear();
        foreach (var g in inv.Devices.GroupBy(d => d.Category).OrderBy(g => g.Key))
        {
            Groups.Add(new DeviceGroup(g.Key, Glyph(g.Key), g.ToList()));
        }

        Warnings.Clear();
        foreach (var w in inv.Warnings) Warnings.Add(w);
        Collected = Loc.Format("Hw_Collected", inv.CollectedAt.LocalDateTime);
    }

    private static string Glyph(HardwareCategory c) => c switch
    {
        HardwareCategory.Cpu => "\uE950",
        HardwareCategory.Gpu => "\uE7F4",
        HardwareCategory.Memory => "\uE964",
        HardwareCategory.Storage => "\uEDA2",
        HardwareCategory.Network => "\uE839",
        HardwareCategory.Motherboard => "\uE9F5",
        HardwareCategory.System => "\uE977",
        HardwareCategory.Battery => "\uE83F",
        HardwareCategory.Display => "\uE7F8",
        HardwareCategory.Audio => "\uE767",
        HardwareCategory.Usb => "\uE88E",
        _ => "\uE772",
    };
}
