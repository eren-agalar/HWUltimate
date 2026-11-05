using System.Windows.Controls;
using System.Windows.Input;
using NovaHardware.App.ViewModels;

namespace NovaHardware.App.Views;

public partial class SensorExplorerView : UserControl
{
    public SensorExplorerView() => InitializeComponent();

    private SensorExplorerViewModel? Vm => DataContext as SensorExplorerViewModel;

    /// <summary>Double-click a category / device / group row to expand or collapse it.</summary>
    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is System.Windows.FrameworkElement { DataContext: ExplorerRow { IsContainer: true } row })
        {
            Vm?.ToggleCommand.Execute(row);
            e.Handled = true;
        }
    }

    /// <summary>Keyboard: Left collapses, Right expands, Enter / Space toggles, F toggles favourite, H hides.</summary>
    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { SelectedRow: { } row } vm) return;
        switch (e.Key)
        {
            case Key.Left when row.IsContainer && row.IsExpanded:
            case Key.Right when row.IsContainer && !row.IsExpanded:
            case Key.Enter or Key.Space when row.IsContainer:
                vm.ToggleCommand.Execute(row);
                e.Handled = true;
                break;
            case Key.F when row.IsSensor:
                vm.ToggleFavoriteCommand.Execute(row);
                e.Handled = true;
                break;
            case Key.H when row.IsSensor:
                vm.ToggleHiddenCommand.Execute(row);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                vm.CopyRowCommand.Execute(row);
                e.Handled = true;
                break;
        }
    }
}
