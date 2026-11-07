using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HWUltimate.Domain.Sensors;
using HWUltimate.UI.Localization;

namespace HWUltimate.UI.Converters;

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

/// <summary>Visible column → fixed width (ConverterParameter), hidden → 0.</summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? new GridLength(double.Parse((string)parameter, CultureInfo.InvariantCulture)) : new GridLength(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Depth / indent in pixels → left margin.</summary>
public sealed class IndentToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is double d ? d : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#RRGGBB" → brush (invalid input → transparent). MultiBinding variant takes [hex, opacity].</summary>
public sealed class HexToBrushConverter : IValueConverter, IMultiValueConverter
{
    public static HexToBrushConverter Instance { get; } = new();

    private static Brush Make(object? hex, double opacity)
    {
        if (hex is not string s || !HWUltimate.Core.Appearance.Rgb.TryParse(s, out var c)) return Brushes.Transparent;
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Make(value, 1);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        Make(values.Length > 0 ? values[0] : null, values.Length > 1 && values[1] is double o ? o : 1);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0..1 → "72 %".</summary>
public sealed class PercentConverter : IValueConverter
{
    public static PercentConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double d ? $"{d * 100:0} %" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
