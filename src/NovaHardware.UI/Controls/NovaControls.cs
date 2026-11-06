using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NovaHardware.UI.Themes;

namespace NovaHardware.UI.Controls;

/// <summary>Square or labelled button showing a Nova icon. Styled by "Nova.IconButton" (implicit).</summary>
public class NovaIconButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(NovaIconButton), new PropertyMetadata("Other"));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(NovaIconButton), new PropertyMetadata(null));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(NovaIconButton), new PropertyMetadata(15.0));

    static NovaIconButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(NovaIconButton), new FrameworkPropertyMetadata(typeof(NovaIconButton)));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
}

/// <summary>Search text box with icon, placeholder and clear button. Escape clears.</summary>
public class NovaSearchBox : TextBox
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(NovaSearchBox), new PropertyMetadata(string.Empty));

    public static RoutedCommand ClearCommand { get; } = new(nameof(ClearCommand), typeof(NovaSearchBox));

    static NovaSearchBox() => DefaultStyleKeyProperty.OverrideMetadata(typeof(NovaSearchBox), new FrameworkPropertyMetadata(typeof(NovaSearchBox)));

    public NovaSearchBox()
    {
        CommandBindings.Add(new CommandBinding(ClearCommand, (_, _) => { Clear(); Focus(); }));
    }

    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Text.Length > 0)
        {
            Clear();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}

public enum NovaStatus { Inactive = 0, Normal, Warning, Critical, Info }

/// <summary>Small status dot with optional text. Colour comes from status tokens only.</summary>
public class NovaStatusIndicator : Control
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(NovaStatus), typeof(NovaStatusIndicator), new PropertyMetadata(NovaStatus.Inactive));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(NovaStatusIndicator), new PropertyMetadata(null));

    static NovaStatusIndicator() => DefaultStyleKeyProperty.OverrideMetadata(typeof(NovaStatusIndicator), new FrameworkPropertyMetadata(typeof(NovaStatusIndicator)));

    public NovaStatus Status { get => (NovaStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}

/// <summary>Expand / collapse chevron. Rotates with a short animation, or instantly when animations are disabled.</summary>
public class NovaChevron : Icon
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(NovaChevron),
        new PropertyMetadata(false, (d, e) => ((NovaChevron)d).Rotate((bool)e.NewValue)));

    private readonly RotateTransform _rotate = new();

    public NovaChevron()
    {
        Kind = "Chevron";
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotate;
    }

    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    private void Rotate(bool expanded)
    {
        var to = expanded ? 90.0 : 0.0;
        if (!ThemeManager.AnimationsEnabled || !IsLoaded)
        {
            _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            _rotate.Angle = to;
            return;
        }

        _rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase() });
    }
}

/// <summary>Themed message dialog replacing the system MessageBox.</summary>
public sealed class NovaDialog : Window
{
    private NovaDialog(string title, string message, string primary, string? secondary, bool danger)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "Size.Font");

        var primaryButton = new Button { Content = primary, IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        primaryButton.SetResourceReference(StyleProperty, danger ? "Nova.Button.Danger" : "Nova.Button.Primary");
        primaryButton.Click += (_, _) => { DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        if (secondary is not null)
        {
            var secondaryButton = new Button { Content = secondary, IsCancel = true, MinWidth = 96 };
            secondaryButton.Click += (_, _) => { DialogResult = false; };
            buttons.Children.Add(secondaryButton);
        }

        buttons.Children.Add(primaryButton);
        var titleText = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 0, 8) };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        var panel = new StackPanel { Children = { titleText, body, buttons } };
        var card = new Border { Padding = new Thickness(24), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(16), Child = panel, MinWidth = 340 };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Content = card;
    }

    public static bool Show(Window? owner, string title, string message, string primary = "OK", string? secondary = null, bool danger = false)
    {
        var d = new NovaDialog(title, message, primary, secondary, danger);
        if (owner is { IsLoaded: true }) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true;
    }
}

/// <summary>True when an integer is non-zero (used for "has text" triggers).</summary>
public sealed class NonZeroConverter : System.Windows.Data.IValueConverter
{
    public static NonZeroConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is int i && i != 0;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the value is not null / empty.</summary>
public sealed class NotNullConverter : System.Windows.Data.IValueConverter
{
    public static NotNullConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not null and not "";

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
