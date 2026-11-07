using System.Windows;
using System.Windows.Controls;
using HWUltimate.App.ViewModels;
using HWUltimate.Domain.Reports;

namespace HWUltimate.App.Views;

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
