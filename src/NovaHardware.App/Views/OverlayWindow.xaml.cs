using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NovaHardware.App.Services;
using NovaHardware.Core.Appearance;
using NovaHardware.Core.Overlay;

namespace NovaHardware.App.Views;

/// <summary>Borderless, always-on-top, non-activating overlay. Optional click-through via WS_EX_TRANSPARENT.</summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x8000000;

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(OverlayWindow), new PropertyMetadata(Brushes.White));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(OverlayWindow), new PropertyMetadata(Brushes.SteelBlue));
    public static readonly DependencyProperty ShowGraphsProperty = DependencyProperty.Register(nameof(ShowGraphs), typeof(bool), typeof(OverlayWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(nameof(ShowLabels), typeof(bool), typeof(OverlayWindow), new PropertyMetadata(true));

    private StackPanel? _panel;
    private OverlayOrientation _orientation;
    private bool _clickThrough;

    public OverlayWindow(OverlayController controller)
    {
        InitializeComponent();
        Lines.ItemsSource = controller.Items;
        Grid.SetIsSharedSizeScope(Lines, true);
        SourceInitialized += (_, _) => ApplyClickThrough(_clickThrough);
    }

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public bool ShowGraphs { get => (bool)GetValue(ShowGraphsProperty); set => SetValue(ShowGraphsProperty, value); }
    public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

    private static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        var c = Rgb.TryParse(hex, out var rgb) ? rgb : new Rgb(17, 21, 29);
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public void ApplySettings(OverlaySettings s)
    {
        Frame.Background = Brush(s.BackgroundColor, s.BackgroundOpacity);
        TextBrush = Brush(s.TextColor);
        AccentBrush = Brush(s.AccentColor);
        FontSize = s.FontSize;
        Scale.ScaleX = Scale.ScaleY = s.Scale;
        ShowGraphs = s.ShowGraphs;
        ShowLabels = s.ShowLabels;
        _orientation = s.Orientation;
        if (_panel is not null) _panel.Orientation = s.Orientation == OverlayOrientation.Horizontal ? Orientation.Horizontal : Orientation.Vertical;
        var area = SystemParameters.VirtualScreenWidth > 0
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : SystemParameters.WorkArea;
        // Restore the saved position only if it is still on a screen.
        if (s.Left is { } l && s.Top is { } t && area.Contains(new Point(l + 10, t + 10))) MoveTo(l, t);
        else MoveTo(SystemParameters.WorkArea.Left + 24, SystemParameters.WorkArea.Top + 24);
        ApplyClickThrough(s.ClickThrough);
    }

    public void MoveTo(double left, double top)
    {
        Left = left;
        Top = top;
    }

    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        _panel = (StackPanel)sender;
        _panel.Orientation = _orientation == OverlayOrientation.Horizontal ? Orientation.Horizontal : Orientation.Vertical;
    }

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_clickThrough && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public void SetAutoHidden(bool hidden) => Opacity = hidden ? 0 : 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>Tool window, never activated; with click-through, mouse input passes to the window underneath.</summary>
    public void ApplyClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var style = (long)GetWindowLongPtr(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate | WsExLayered;
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)style);
    }
}
