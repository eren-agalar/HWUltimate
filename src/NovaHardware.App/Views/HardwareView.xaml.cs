using System.Windows.Controls;

namespace NovaHardware.App.Views;

public partial class HardwareView : UserControl
{
    public HardwareView() => InitializeComponent();
}

public sealed class InverseBool : System.Windows.Data.IValueConverter
{
    public static InverseBool Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
}
