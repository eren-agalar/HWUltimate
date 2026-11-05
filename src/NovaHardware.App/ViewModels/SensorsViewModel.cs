using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.ViewModels;

/// <summary>One sensor row, updated in place every poll to keep the grid smooth.</summary>
public sealed partial class SensorRow : ObservableObject
{
    public required string SensorId { get; init; }
    public required string DeviceName { get; init; }
    public required HardwareCategory Category { get; init; }
    public required string Name { get; init; }
    public required SensorType Type { get; init; }
    public required string Provider { get; init; }
    public required SensorUnit Unit { get; init; }

    public string TypeName => Type.ToString();
    public string GroupKey => $"{DeviceName}  ·  {Category}";

    [ObservableProperty] private string _current = string.Empty;
    [ObservableProperty] private string _minimum = string.Empty;
    [ObservableProperty] private string _maximum = string.Empty;
    [ObservableProperty] private string _average = string.Empty;
    [ObservableProperty] private DataQuality _quality;

    public void Apply(SensorReading r)
    {
        var c = Loc.Instance.Culture;
        var na = Loc.Get("Common_NA");
        Current = r.Unit.FormatValue(r.Value, c, na);
        Minimum = r.Unit.FormatValue(r.Minimum, c, na);
        Maximum = r.Unit.FormatValue(r.Maximum, c, na);
        Average = r.Unit.FormatValue(r.Average, c, na);
        Quality = r.Quality;
    }
}

public sealed partial class SensorsViewModel : ObservableObject
{
    private readonly ISensorPollingService _polling;
    private readonly Dictionary<string, SensorRow> _rows = new(StringComparer.Ordinal);

    public SensorsViewModel(ISensorPollingService polling)
    {
        _polling = polling;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.GroupKey)));
        View.Filter = o => o is SensorRow r && Matches(r);
    }

    public ObservableCollection<SensorRow> Rows { get; } = [];

    public ICollectionView View { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;

    partial void OnSearchTextChanged(string value) => View.Refresh();

    private bool Matches(SensorRow r) =>
        string.IsNullOrWhiteSpace(SearchText)
        || r.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
        || r.DeviceName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
        || r.TypeName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);

    public void Update(SensorSnapshot snapshot)
    {
        var devices = snapshot.Devices.ToDictionary(d => d.DeviceId, StringComparer.Ordinal);
        var added = false;
        foreach (var r in snapshot.Sensors)
        {
            if (!_rows.TryGetValue(r.SensorId, out var row))
            {
                devices.TryGetValue(r.DeviceId, out var device);
                row = new SensorRow
                {
                    SensorId = r.SensorId,
                    DeviceName = device?.Name ?? r.DeviceId,
                    Category = device?.Category ?? HardwareCategory.Other,
                    Name = r.Name,
                    Type = r.Type,
                    Provider = r.ProviderName,
                    Unit = r.Unit,
                };
                _rows[r.SensorId] = row;
                Rows.Add(row);
                added = true;
            }

            row.Apply(r);
        }

        if (added)
        {
            View.Refresh();
        }

        Summary = Loc.Format("Sensors_Count", snapshot.Sensors.Count, snapshot.Devices.Count);
    }

    [RelayCommand]
    private void ResetStatistics() => _polling.ResetStatistics();
}
