using System.Diagnostics;
using System.Windows;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using NovaHardware.UI.Themes;
using SkiaSharp;

namespace NovaHardware.App.Services;

public static class Ui
{
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Serilog.Log.Warning(ex, "Could not open {Path}", path);
        }
    }

    /// <summary>Relaunches the app elevated (UAC prompt) and closes this instance.</summary>
    public static void RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--allow-multiple") { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the UAC prompt — keep running.
        }
    }
}

/// <summary>Chart paints derived from the active theme so charts follow dark / light switches.</summary>
public static class ChartTheme
{
    public static SKColor Sk(string brushKey, byte alpha = 255)
    {
        var c = ThemeManager.Color(brushKey);
        return new SKColor(c.R, c.G, c.B, alpha);
    }

    public static LineSeries<TPoint> Line<TPoint>(string name, IReadOnlyCollection<TPoint> values, string brushKey, bool fill = true) => new()
    {
        Name = name,
        Values = values,
        GeometrySize = 0,
        LineSmoothness = 0.35,
        Stroke = new SolidColorPaint(Sk(brushKey)) { StrokeThickness = 2 },
        Fill = fill ? new SolidColorPaint(Sk(brushKey, 36)) : null,
        AnimationsSpeed = TimeSpan.Zero,
    };

    public static Axis TimeAxis(string format = "HH:mm:ss") => new DateTimeAxis(TimeSpan.FromSeconds(1), d => d.ToString(format))
    {
        LabelsPaint = new SolidColorPaint(Sk("Brush.TextMuted")),
        TextSize = 11,
        SeparatorsPaint = null,
        AnimationsSpeed = TimeSpan.Zero,
    };

    public static Axis ValueAxis(double? min = null, double? max = null, string? unit = null) => new()
    {
        MinLimit = min,
        MaxLimit = max,
        LabelsPaint = new SolidColorPaint(Sk("Brush.TextMuted")),
        TextSize = 11,
        Labeler = v => unit is null ? v.ToString("0.#") : $"{v:0.#} {unit}",
        SeparatorsPaint = new SolidColorPaint(Sk("Brush.Line")) { StrokeThickness = 1 },
        AnimationsSpeed = TimeSpan.Zero,
    };

    public static SolidColorPaint LegendText() => new(Sk("Brush.Text"));

    public static SolidColorPaint TooltipBackground() => new(Sk("Brush.Panel"));
}
