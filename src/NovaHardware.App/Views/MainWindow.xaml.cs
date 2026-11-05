using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NovaHardware.App.ViewModels;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        StateChanged += (_, _) =>
        {
            // WindowChrome windows overhang the screen by the resize border when maximised.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        };
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Translates a resource key held in data (e.g. nav page keys) for display.</summary>
public sealed class TrConverter : IValueConverter
{
    public static TrConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Loc.Get(key) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
