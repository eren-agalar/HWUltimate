using System.ComponentModel;
using System.Windows.Controls;
using NovaHardware.App.ViewModels;
using NovaHardware.UI.Localization;

namespace NovaHardware.App.Views;

public partial class OverlayView : UserControl
{
    public OverlayView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is OverlayViewModel vm)
            {
                vm.RefreshGpus();
                UpdateToggleText(vm);
                vm.Controller.PropertyChanged += (_, args) => { if (args.PropertyName == "IsVisible") UpdateToggleText(vm); };
            }
        };
    }

    private void UpdateToggleText(OverlayViewModel vm) =>
        ToggleText.Text = Loc.Get(vm.Controller.IsVisible ? "Overlay_Hide" : "Overlay_Show");
}
