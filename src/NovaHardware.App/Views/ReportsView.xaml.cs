using System.Windows;
using System.Windows.Controls;
using NovaHardware.App.ViewModels;
using NovaHardware.Domain.Reports;

namespace NovaHardware.App.Views;

public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    private void Format_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: ReportFormat f } rb && DataContext is ReportsViewModel vm) rb.IsChecked = vm.Format == f;
    }

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: ReportFormat f } && DataContext is ReportsViewModel vm) vm.Format = f;
    }
}
