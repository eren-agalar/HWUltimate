using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace HWUltimate.UI.Localization;

/// <summary>
/// Runtime-switchable localisation. XAML binds through <see cref="TrExtension"/>; changing <see cref="Culture"/>
/// refreshes every bound string immediately without restarting the app.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("HWUltimate.UI.Localization.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    public static IReadOnlyList<(string Culture, string DisplayName)> Languages { get; } =
    [
        ("", "System default"),
        ("en-US", "English"),
        ("tr-TR", "Türkçe"),
    ];

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            _culture = value;
            CultureInfo.CurrentUICulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        }
    }

    public string this[string key] => Resources.GetString(key, _culture) ?? key;

    public static string Get(string key) => Instance[key];

    public static string Format(string key, params object?[] args) => string.Format(Instance._culture, Instance[key], args);

    public void Apply(string cultureName) =>
        Culture = string.IsNullOrWhiteSpace(cultureName) ? CultureInfo.InstalledUICulture : CultureInfo.GetCultureInfo(cultureName);
}

/// <summary>XAML usage: <c>Text="{loc:Tr Nav_Sensors}"</c>.</summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
