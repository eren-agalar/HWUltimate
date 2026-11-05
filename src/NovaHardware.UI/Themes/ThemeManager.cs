using System.Windows;

namespace NovaHardware.UI.Themes;

public enum AppTheme { Dark, Light }

/// <summary>Swaps the colour dictionary at runtime. All styles use DynamicResource, so the switch is instant.</summary>
public static class ThemeManager
{
    private static readonly Uri DarkUri = new("pack://application:,,,/NovaHardware.UI;component/Themes/Dark.xaml");
    private static readonly Uri LightUri = new("pack://application:,,,/NovaHardware.UI;component/Themes/Light.xaml");

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event EventHandler? Changed;

    public static void Apply(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source == DarkUri || d.Source == LightUri);
        var replacement = new ResourceDictionary { Source = theme == AppTheme.Dark ? DarkUri : LightUri };
        if (existing is null)
        {
            dictionaries.Insert(0, replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(existing)] = replacement;
        }

        Current = theme;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static System.Windows.Media.Color Color(string brushKey) =>
        Application.Current.TryFindResource(brushKey) is System.Windows.Media.SolidColorBrush b ? b.Color : System.Windows.Media.Colors.Gray;
}
