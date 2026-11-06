using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NovaHardware.UI.Controls;

/// <summary>
/// Nova's line-icon set: 24×24 stroke geometry rendered with the inherited foreground brush, so icons follow the theme,
/// keep identical visual weight at any size and stay crisp at high DPI. Source paths live in tools/icons.json.
/// </summary>
public class Icon : FrameworkElement
{
    private static readonly Dictionary<string, string> PathData = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cpu"] = "M7,7 H17 V17 H7 Z M10,10 H14 V14 H10 Z M9.5,3 V7 M14.5,3 V7 M9.5,17 V21 M14.5,17 V21 M3,9.5 H7 M3,14.5 H7 M17,9.5 H21 M17,14.5 H21",
        ["Gpu"] = "M2,6 H20 V17 H2 Z M20,9 H22 M5,17 V20 M9,17 V20 M13,17 V20 M4.9,11.5 A2.6,2.6 0 1 0 10.1,11.5 A2.6,2.6 0 1 0 4.9,11.5 Z M11.9,11.5 A2.6,2.6 0 1 0 17.1,11.5 A2.6,2.6 0 1 0 11.9,11.5 Z",
        ["Memory"] = "M2,7 H22 V15 H2 Z M5,15 V18 M8.5,15 V18 M12,15 V18 M15.5,15 V18 M19,15 V18 M5,9.5 H8 V12.5 H5 Z M10.5,9.5 H13.5 V12.5 H10.5 Z M16,9.5 H19 V12.5 H16 Z",
        ["Motherboard"] = "M3,3 H21 V21 H3 Z M6,6 H12 V12 H6 Z M15,6 H18 M15,9 H18 M6,16 H18 M6,18.5 H12 M15,12 H18 V18.5 H15 Z",
        ["Storage"] = "M3,6 H21 V18 H3 Z M3,13 H21 M6,15.5 H11 M17.5,15.5 H17.51",
        ["Network"] = "M9,3 H15 V8 H9 Z M3,16 H9 V21 H3 Z M15,16 H21 V21 H15 Z M12,8 V12 M6,16 V12 H18 V16",
        ["Fan"] = "M2.5,12 A9.5,9.5 0 1 0 21.5,12 A9.5,9.5 0 1 0 2.5,12 Z M10.4,12 A1.6,1.6 0 1 0 13.6,12 A1.6,1.6 0 1 0 10.4,12 Z M12.0,10.4 C10.5,6.5 13.5,4.2 16.2,5.6 C16.4,8.6 14.5,10.2 13.4,11.0 M13.39,12.8 C17.51,13.45 18.0,17.2 15.44,18.84 C12.74,17.51 12.31,15.07 12.17,13.71 M10.61,12.8 C7.99,16.05 4.5,14.6 4.36,11.56 C6.86,9.89 9.19,10.73 10.43,11.29",
        ["Battery"] = "M3,8 H19 V16 H3 Z M19,10.5 H21 V13.5 H19 M6,11 V13 M9,11 V13 M12,11 V13",
        ["Power"] = "M13,2 L5,13.5 H11 L10,22 L19,10.5 H13 Z",
        ["PowerSupply"] = "M3,5 H21 V19 H3 Z M5,12 A4,4 0 1 0 13,12 A4,4 0 1 0 5,12 Z M15,8.5 H18 M15,12 H18 M15,15.5 H18",
        ["Thermometer"] = "M10,13.5 V5 A2,2 0 0 1 14,5 V13.5 A4,4 0 1 1 10,13.5 Z M12,9 V17",
        ["Voltage"] = "M2,12 C5,4 8,4 12,12 C16,20 19,20 22,12",
        ["Current"] = "M3,12 H21 M15,7 L20,12 L15,17 M3,7 V17",
        ["Clock"] = "M4.2,17.5 A8.5,8.5 0 1 1 19.8,17.5 M12,13.5 L16,8.5 M12,13.5 H12.01 M5.8,10 H7.2 M12,5.8 V7.2 M18.2,10 H16.8",
        ["Utilization"] = "M3,20 H21 M6,20 V14 M10,20 V9 M14,20 V12 M18,20 V5",
        ["Data"] = "M4,6 C4,2.7 20,2.7 20,6 C20,9.3 4,9.3 4,6 Z M4,6 V18 C4,21.3 20,21.3 20,18 V6 M4,12 C4,15.3 20,15.3 20,12",
        ["Throughput"] = "M8,4 V20 M4,8 L8,4 L12,8 M16,20 V4 M12,16 L16,20 L20,16",
        ["Throttle"] = "M12,3 L22,20 H2 Z M12,9 V14 M12,17 H12.01",
        ["Health"] = "M12,3 L20,6 V12 C20,16.5 16.5,19.7 12,21 C7.5,19.7 4,16.5 4,12 V6 Z M8.5,12 L11,14.5 L15.5,10",
        ["PerCore"] = "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z",
        ["Timing"] = "M4.5,13.5 A7.5,7.5 0 1 0 19.5,13.5 A7.5,7.5 0 1 0 4.5,13.5 Z M12,9.5 V13.5 L14.5,15.5 M9.5,2.5 H14.5 M12,2.5 V6",
        ["Level"] = "M12,3 C12,3 5,11 5,15 A7,7 0 0 0 19,15 C19,11 12,3 12,3 Z",
        ["Info"] = "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 Z M12,11 V16.5 M12,7.8 H12.01",
        ["Display"] = "M3,4 H21 V16 H3 Z M9,20 H15 M12,16 V20",
        ["Usb"] = "M12,3 V17 M9,6 L12,3 L15,6 M12,13 L7,10 V8 M12,15 L17,12 V9.5 M15.5,8 H18.5 V9.5 H15.5 Z M10,19 A2,2 0 1 0 14,19 A2,2 0 1 0 10,19 Z",
        ["Audio"] = "M4,9 H8 L13,5 V19 L8,15 H4 Z M16.5,9 C18,10.5 18,13.5 16.5,15 M19,6.5 C22,9.5 22,14.5 19,17.5",
        ["System"] = "M6,3 H18 V21 H6 Z M9,7 H15 M9,10 H15 M12,17 H12.01",
        ["Star"] = "M12,3 L14.8,8.7 L21,9.6 L16.5,14 L17.6,20.2 L12,17.3 L6.4,20.2 L7.5,14 L3,9.6 L9.2,8.7 Z",
        ["Eye"] = "M2,12 C5,6 19,6 22,12 C19,18 5,18 2,12 Z M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z",
        ["EyeOff"] = "M2,12 C5,6 19,6 22,12 C19,18 5,18 2,12 Z M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z M4,4 L20,20",
        ["Chevron"] = "M9,6 L15,12 L9,18",
        ["Copy"] = "M8,8 H20 V20 H8 Z M16,8 V4 H4 V16 H8",
        ["Search"] = "M4.0,10.5 A6.5,6.5 0 1 0 17.0,10.5 A6.5,6.5 0 1 0 4.0,10.5 Z M15.5,15.5 L21,21",
        ["Export"] = "M12,3 V15 M7,10 L12,15 L17,10 M4,19 H20",
        ["ArrowUp"] = "M12,19 V5 M6,11 L12,5 L18,11",
        ["ArrowDown"] = "M12,5 V19 M6,13 L12,19 L18,13",
        ["ExpandAll"] = "M7,6 L12,11 L17,6 M7,13 L12,18 L17,13",
        ["CollapseAll"] = "M7,11 L12,6 L17,11 M7,18 L12,13 L17,18",
        ["Reset"] = "M4,12 A8,8 0 1 0 6.3,6.3 M4,3.5 V8 H8.5",
        ["Columns"] = "M4,4 H20 V20 H4 Z M10,4 V20 M15,4 V20",
        ["Compact"] = "M4,6 H20 M4,10 H20 M4,14 H20 M4,18 H20",
        ["Detailed"] = "M4,4 H20 V10 H4 Z M4,14 H20 V20 H4 Z",
        ["Cooler"] = "M2.5,12 A9.5,9.5 0 1 0 21.5,12 A9.5,9.5 0 1 0 2.5,12 Z M10.4,12 A1.6,1.6 0 1 0 13.6,12 A1.6,1.6 0 1 0 10.4,12 Z M12.0,10.4 C10.5,6.5 13.5,4.2 16.2,5.6 C16.4,8.6 14.5,10.2 13.4,11.0 M13.39,12.8 C17.51,13.45 18.0,17.2 15.44,18.84 C12.74,17.51 12.31,15.07 12.17,13.71 M10.61,12.8 C7.99,16.05 4.5,14.6 4.36,11.56 C6.86,9.89 9.19,10.73 10.43,11.29",
        ["Other"] = "M4,4 H20 V20 H4 Z M8,12 H8.01 M12,12 H12.01 M16,12 H16.01",
        ["Refresh"] = "M20,12 A8,8 0 1 1 17.7,6.3 M20,3.5 V8 H15.5",
        ["Dashboard"] = "M3,3 H10 V12 H3 Z M14,3 H21 V8 H14 Z M14,12 H21 V21 H14 Z M3,16 H10 V21 H3 Z",
        ["Report"] = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19 M9,12 H16 M9,15.5 H16 M9,19 H13",
        ["Settings"] = "M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z M18.20,12.00 L21.00,12.00 M16.38,16.38 L18.36,18.36 M12.00,18.20 L12.00,21.00 M7.62,16.38 L5.64,18.36 M5.80,12.00 L3.00,12.00 M7.62,7.62 L5.64,5.64 M12.00,5.80 L12.00,3.00 M16.38,7.62 L18.36,5.64 M5.8,12 A6.2,6.2 0 1 0 18.2,12 A6.2,6.2 0 1 0 5.8,12 Z",
        ["Bell"] = "M6,16 V11 A6,6 0 0 1 18,11 V16 L20,18 H4 Z M10,21 H14",
        ["History"] = "M4,12 A8,8 0 1 0 6.3,6.3 M4,3.5 V8 H8.5 M12,8 V12 L15,14",
        ["Close"] = "M6,6 L18,18 M18,6 L6,18",
        ["ChevronDown"] = "M6,9 L12,15 L18,9",
        ["Pin"] = "M9,3 H15 M10,3 V9 L7,13 H17 L14,9 V3 M12,13 V21",
        ["Overlay"] = "M3,5 H21 V19 H3 Z M13,8 H19 V12 H13 Z",
        ["Filter"] = "M3,5 H21 L14,13 V19 L10,21 V13 Z",
        ["Sort"] = "M7,4 V20 M3,8 L7,4 L11,8 M17,20 V4 M13,16 L17,20 L21,16",
        ["Palette"] = "M12,3 A9,9 0 1 0 12,21 C13.5,21 14,20 14,19 C14,17.5 13,17 13,16 C13,15 14,14.5 15,14.5 H17 A4,4 0 0 0 21,10.5 C21,6.4 17,3 12,3 Z M7.5,11 H7.51 M10,7 H10.01 M15,7 H15.01",
        ["Keyboard"] = "M2,6 H22 V18 H2 Z M6,10 H6.01 M10,10 H10.01 M14,10 H14.01 M18,10 H18.01 M7,14 H17",
        ["Minimize"] = "M5,12 H19",
        ["Maximize"] = "M5,5 H19 V19 H5 Z",
        ["Restore"] = "M8,8 H19 V19 H8 Z M5,16 V5 H16",
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("Other", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(true));
    }

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    public static IReadOnlyCollection<string> Kinds => PathData.Keys;

    public static Geometry? GetGeometry(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(kind, out var g)) return g;
            if (!PathData.TryGetValue(kind, out var data)) return kind == "Other" ? null : GetGeometry("Other");
            g = Geometry.Parse(data);
            g.Freeze();
            Cache[kind] = g;
            return g;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var geometry = GetGeometry(Kind);
        if (geometry is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var size = Math.Min(ActualWidth, ActualHeight);
        var scale = size / 24.0;
        var pen = new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, (ActualWidth - size) / 2, (ActualHeight - size) / 2));
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
    }
}

/// <summary>Maps hardware categories / sensor groups / node kinds to icon kinds.</summary>
public static class IconMap
{
    public static string ForCategory(NovaHardware.Domain.Hardware.HardwareCategory c) => c switch
    {
        NovaHardware.Domain.Hardware.HardwareCategory.Cpu => "Cpu",
        NovaHardware.Domain.Hardware.HardwareCategory.Gpu => "Gpu",
        NovaHardware.Domain.Hardware.HardwareCategory.Memory => "Memory",
        NovaHardware.Domain.Hardware.HardwareCategory.Motherboard or NovaHardware.Domain.Hardware.HardwareCategory.SuperIo or NovaHardware.Domain.Hardware.HardwareCategory.EmbeddedController => "Motherboard",
        NovaHardware.Domain.Hardware.HardwareCategory.Storage => "Storage",
        NovaHardware.Domain.Hardware.HardwareCategory.Network => "Network",
        NovaHardware.Domain.Hardware.HardwareCategory.Cooler => "Cooler",
        NovaHardware.Domain.Hardware.HardwareCategory.Battery => "Battery",
        NovaHardware.Domain.Hardware.HardwareCategory.PowerSupply => "PowerSupply",
        NovaHardware.Domain.Hardware.HardwareCategory.Display => "Display",
        NovaHardware.Domain.Hardware.HardwareCategory.Audio => "Audio",
        NovaHardware.Domain.Hardware.HardwareCategory.Usb => "Usb",
        NovaHardware.Domain.Hardware.HardwareCategory.System => "System",
        _ => "Other",
    };
}
