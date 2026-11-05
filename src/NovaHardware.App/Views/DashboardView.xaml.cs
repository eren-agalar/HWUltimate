using System.Windows.Controls;

namespace NovaHardware.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}

/// <summary>Visible when a count is zero (empty-state messages).</summary>
public sealed class ZeroToVisibility : System.Windows.Data.IValueConverter
{
    public static ZeroToVisibility Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
