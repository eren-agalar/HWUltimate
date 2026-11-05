using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NovaHardware.App.ViewModels;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.Views;

public partial class StabilityView : UserControl
{
    public StabilityView() => InitializeComponent();

    private void KindRadio_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: StressKindOption option } rb && DataContext is StabilityViewModel vm)
        {
            rb.IsChecked = ReferenceEquals(vm.SelectedKind, option);
        }
    }
}

public sealed class MinutesConverter : IValueConverter
{
    public static MinutesConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.Format("Stress_Minutes", value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ErrorBrush : IValueConverter
{
    public static ErrorBrush Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.TryFindResource(value is long and > 0 ? "Brush.Bad" : "Brush.Text") as Brush ?? Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
