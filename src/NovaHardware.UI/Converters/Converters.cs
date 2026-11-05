using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NovaHardware.Domain.Sensors;
using NovaHardware.UI.Localization;

namespace NovaHardware.UI.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        (value is null or "") ^ Invert ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class QualityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is DataQuality q ? q switch
        {
            DataQuality.Good => "Brush.Good",
            DataQuality.Stale => "Brush.Warn",
            DataQuality.Suspect => "Brush.Bad",
            _ => "Brush.TextMuted",
        } : "Brush.TextMuted";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class QualityToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DataQuality q ? Loc.Get($"Quality_{q}") : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a value in [0,100] to a status brush (good / warn / bad) with thresholds passed as "warn,bad".</summary>
public sealed class ThresholdToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "75,90").Split(',');
        var warn = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var bad = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var key = value is double d ? d >= bad ? "Brush.Bad" : d >= warn ? "Brush.Warn" : "Brush.Good" : "Brush.TextMuted";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string s ? Enum.Parse(targetType, s) : Binding.DoNothing;
}
