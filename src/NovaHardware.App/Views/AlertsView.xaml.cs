using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace NovaHardware.App.Views;

public partial class AlertsView : UserControl
{
    public AlertsView() => InitializeComponent();
}

/// <summary>Visible when a count is zero (empty-state messages).</summary>
public sealed class ZeroToVisibility : IValueConverter
{
    public static ZeroToVisibility Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
