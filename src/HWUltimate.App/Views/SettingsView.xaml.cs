using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HWUltimate.App.ViewModels;

namespace HWUltimate.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is SettingsViewModel vm)
            {
                vm.PropertyChanged += Vm_PropertyChanged;
                ShowSection(vm.SelectedSection.Key);
            }
        };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedSection) && sender is SettingsViewModel vm) ShowSection(vm.SelectedSection.Key);
        if (e.PropertyName == nameof(SettingsViewModel.Theme)) foreach (var rb in FindRadios(this)) Theme_Loaded(rb, new RoutedEventArgs());
    }

    private void ShowSection(string key)
    {
        foreach (var page in new FrameworkElement[] { Appearance, Sensors, General })
        {
            page.Visibility = page.Name == key ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Theme_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: ThemeOption t } rb && DataContext is SettingsViewModel vm) rb.IsChecked = vm.Theme == t.Choice;
    }

    private static IEnumerable<RadioButton> FindRadios(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton { DataContext: ThemeOption } rb) yield return rb;
            foreach (var nested in FindRadios(child)) yield return nested;
        }
    }

}
