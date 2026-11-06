using System.Windows;
using NovaHardware.App.Services;
using NovaHardware.App.ViewModels;

namespace NovaHardware.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, OverlayController overlay)
    {
        InitializeComponent();
        DataContext = viewModel;
        // The global overlay shortcut is registered against this window's handle.
        SourceInitialized += (_, _) => overlay.Attach(this);
        StateChanged += (_, _) =>
        {
            // WindowChrome windows overhang the screen by the resize border when maximised.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxIcon.Kind = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        };
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
