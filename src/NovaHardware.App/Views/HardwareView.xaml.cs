using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using NovaHardware.Core.HardwareInfo;

namespace NovaHardware.App.Views;

public partial class HardwareView : UserControl
{
    public static HardwareInfoFormat TextFormat => HardwareInfoFormat.Text;
    public static HardwareInfoFormat JsonFormat => HardwareInfoFormat.Json;
    public static HardwareInfoFormat HtmlFormat => HardwareInfoFormat.Html;

    public HardwareView() => InitializeComponent();

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is { } menu)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = ExportButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}

public sealed class InverseBool : IValueConverter
{
    public static InverseBool Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class PositiveToVisibility : IValueConverter
{
    public static PositiveToVisibility Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
