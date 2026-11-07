using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using HWUltimate.UI.Themes;

namespace HWUltimate.UI.Controls;

/// <summary>
/// The HWUltimate logo drawn as vectors (crisp at any size and DPI): a gradient badge holding a processor chip with a
/// sensor pulse. When animated it fades / scales in, the pulse is traced periodically and a light sheen sweeps the badge.
/// Animation runs at a low frame rate, pauses while the window is minimised or inactive, and is off when the user turns
/// animations off. Geometry matches tools/logo.py, which renders the .ico / .png files.
/// </summary>
public sealed class HwuLogo : Viewbox
{
    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(nameof(IsAnimated), typeof(bool), typeof(HwuLogo),
        new PropertyMetadata(true, (d, _) => ((HwuLogo)d).UpdateAnimation()));

    private static readonly Point[] PulsePoints = [new(19, 34), new(25, 34), new(28, 27), new(32, 41), new(36, 23), new(39, 34), new(45, 34)];
    private const double PulseThickness = 3.0;
    private const int FrameRate = 30;

    private readonly Polyline _pulse;
    private readonly TranslateTransform _sheenShift = new(-80, 0);
    private readonly ScaleTransform _scale = new(1, 1, 32, 32);
    private readonly Canvas _canvas;
    private Window? _window;
    private bool _running;
    private bool _introPlayed;

    public HwuLogo()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        var white = Brushes.White;
        var badgeGeometry = new RectangleGeometry(new Rect(2, 2, 60, 60), 15, 15);

        _canvas = new Canvas { Width = 64, Height = 64, RenderTransform = _scale };
        var gradient = new LinearGradientBrush(Color.FromRgb(124, 92, 255), Color.FromRgb(34, 211, 238), new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        _canvas.Children.Add(new Path { Data = badgeGeometry, Fill = gradient });
        _canvas.Children.Add(new Path
        {
            Data = new RectangleGeometry(new Rect(2, 2, 60, 32), 15, 15),
            Fill = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            Clip = badgeGeometry,
        });

        // Sheen: a soft diagonal light band sweeping across the badge.
        var sheenBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
            GradientStops = { new GradientStop(Color.FromArgb(0, 255, 255, 255), 0), new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
        };
        var sheen = new Rectangle { Width = 26, Height = 120, Fill = sheenBrush, IsHitTestVisible = false };
        var sheenTransform = new TransformGroup { Children = { new RotateTransform(25, 13, 60), _sheenShift } };
        sheen.RenderTransform = sheenTransform;
        Canvas.SetTop(sheen, -28);
        var sheenHost = new Canvas { Width = 64, Height = 64, Clip = badgeGeometry, IsHitTestVisible = false };
        sheenHost.Children.Add(sheen);
        _canvas.Children.Add(sheenHost);

        // Chip with pins.
        const double w = 2.6;
        var chip = new Rectangle { Width = 34, Height = 34, RadiusX = 6, RadiusY = 6, Stroke = white, StrokeThickness = w };
        Canvas.SetLeft(chip, 15);
        Canvas.SetTop(chip, 15);
        _canvas.Children.Add(chip);
        var pins = new GeometryGroup();
        foreach (var p in new[] { 24.0, 32, 40 })
        {
            pins.Children.Add(new LineGeometry(new Point(p, 8.5), new Point(p, 15)));
            pins.Children.Add(new LineGeometry(new Point(p, 49), new Point(p, 55.5)));
            pins.Children.Add(new LineGeometry(new Point(8.5, p), new Point(15, p)));
            pins.Children.Add(new LineGeometry(new Point(49, p), new Point(55.5, p)));
        }

        pins.Freeze();
        _canvas.Children.Add(new Path { Data = pins, Stroke = white, StrokeThickness = w, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

        _pulse = new Polyline
        {
            Points = new PointCollection(PulsePoints),
            Stroke = white,
            StrokeThickness = PulseThickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _canvas.Children.Add(_pulse);
        Child = _canvas;

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    /// <summary>Whether the logo animates (still subject to the global "Animations" setting and window state).</summary>
    public bool IsAnimated
    {
        get => (bool)GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    private static double PulseLength()
    {
        double total = 0;
        for (var i = 1; i < PulsePoints.Length; i++) total += (PulsePoints[i] - PulsePoints[i - 1]).Length;
        return total;
    }

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += OnWindowChanged;
            _window.Activated += OnWindowChanged;
            _window.Deactivated += OnWindowChanged;
        }

        ThemeManager.Changed += OnThemeChanged;
        UpdateAnimation();
    }

    private void Detach()
    {
        if (_window is not null)
        {
            _window.StateChanged -= OnWindowChanged;
            _window.Activated -= OnWindowChanged;
            _window.Deactivated -= OnWindowChanged;
            _window = null;
        }

        ThemeManager.Changed -= OnThemeChanged;
        Stop();
    }

    private void OnWindowChanged(object? sender, EventArgs e) => UpdateAnimation();

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateAnimation();

    private bool ShouldRun => IsAnimated && IsLoaded && IsVisible && ThemeManager.AnimationsEnabled
                              && _window is { WindowState: not WindowState.Minimized, IsActive: true };

    private void UpdateAnimation()
    {
        if (ShouldRun) Start();
        else Stop();
    }

    private void Start()
    {
        if (_running) return;
        _running = true;

        if (!_introPlayed)
        {
            _introPlayed = true;
            var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var grow = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(grow, 60);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380));
            _canvas.BeginAnimation(OpacityProperty, fade);
        }

        // Pulse trace: dash pattern in multiples of stroke thickness; offset runs from "hidden" to "drawn", then holds.
        var length = PulseLength() / PulseThickness;
        _pulse.StrokeDashArray = new DoubleCollection([length, length]);
        var trace = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(4) };
        trace.KeyFrames.Add(new DiscreteDoubleKeyFrame(length, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        trace.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        trace.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4))));
        Timeline.SetDesiredFrameRate(trace, FrameRate);
        _pulse.BeginAnimation(Shape.StrokeDashOffsetProperty, trace);

        var sweep = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(7) };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-80, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-80, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.6))));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(90, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(6.0)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(90, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(7))));
        Timeline.SetDesiredFrameRate(sweep, FrameRate);
        _sheenShift.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private void Stop()
    {
        if (!_running && _pulse.StrokeDashArray.Count == 0) return;
        _running = false;
        // Remove clocks and show the finished, static logo.
        _pulse.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        _pulse.StrokeDashArray = new DoubleCollection();
        _pulse.StrokeDashOffset = 0;
        _sheenShift.BeginAnimation(TranslateTransform.XProperty, null);
        _sheenShift.X = -80;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        _canvas.BeginAnimation(OpacityProperty, null);
        _canvas.Opacity = 1;
    }
}
