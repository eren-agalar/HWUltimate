using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NovaHardware.App.ViewModels;

namespace NovaHardware.App.Views;

public partial class SensorExplorerView : UserControl
{
    public SensorExplorerView() => InitializeComponent();

    private SensorExplorerViewModel? Vm => DataContext as SensorExplorerViewModel;

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = MoreButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>Double-click a category / device / group row to expand or collapse it.</summary>
    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: ExplorerRow { IsContainer: true } row })
        {
            Vm?.ToggleCommand.Execute(row);
            e.Handled = true;
        }
    }

    /// <summary>Keyboard: ←/→ collapse/expand, Enter/Space toggle, F favourite, P pin, H hide, Ctrl+C copy.</summary>
    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { SelectedRow: { } row } vm) return;
        switch (e.Key)
        {
            case Key.Left when row.IsContainer && row.IsExpanded:
            case Key.Right when row.IsContainer && !row.IsExpanded:
            case Key.Enter or Key.Space when row.IsContainer:
                vm.ToggleCommand.Execute(row);
                break;
            case Key.F when row.IsSensor:
                vm.ToggleFavoriteCommand.Execute(row);
                break;
            case Key.P when row.IsSensor:
                vm.TogglePinnedCommand.Execute(row);
                break;
            case Key.H when row.IsSensor:
                vm.ToggleHiddenCommand.Execute(row);
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                vm.CopyRowCommand.Execute(row);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
