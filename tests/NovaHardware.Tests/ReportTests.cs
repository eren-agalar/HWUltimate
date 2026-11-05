using System.Text.Json;
using NovaHardware.Core.Reports;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Reports;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Tests;

public class ReportTests
{
    private static ReportData Sample()
    {
        var devices = new[] { new HardwareDevice("cpu", "Test <CPU>", HardwareCategory.Cpu, "lhm") };
        var snap = Snapshots.Of(DateTimeOffset.Now, devices,
            Snapshots.Reading("t", "cpu", "Package, \"hot\"", SensorType.Temperature, 61.25) with { Minimum = 40, Maximum = 70, Average = 55 },
            Snapshots.Reading("v", "cpu", "Vcore", SensorType.Voltage, null, DataQuality.Missing));
        var inventory = new HardwareInventory(DateTimeOffset.Now, new SystemSummary { Processor = "Test CPU", PhysicalCores = 8, LogicalProcessors = 16 },
            [new DeviceInfo { Id = "x", Name = "Board", Category = HardwareCategory.Motherboard, Source = "SMBIOS", Properties = [new("BIOS", "1.0")] }], []);
        return new ReportData { ApplicationVersion = "1.2.3", Sensors = snap, Inventory = inventory };
    }

    private readonly ReportExportService _svc = new();

    [Fact]
    public void Html_is_encoded_and_shows_na_for_missing()
    {
        var html = _svc.Render(Sample(), ReportFormat.Html);
        Assert.Contains("Test &lt;CPU&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<CPU>", html, StringComparison.Ordinal);
        Assert.Contains("61.3 °C", html, StringComparison.Ordinal);
        Assert.Contains("N/A", html, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_is_valid_and_keeps_nulls_out()
    {
        using var doc = JsonDocument.Parse(_svc.Render(Sample(), ReportFormat.Json));
        var sensors = doc.RootElement.GetProperty("sensors");
        Assert.Equal(2, sensors.GetArrayLength());
        Assert.Equal(61.25, sensors[0].GetProperty("Value").GetDouble());
        Assert.Equal("°C", sensors[0].GetProperty("unit").GetString());
        Assert.False(sensors[1].TryGetProperty("Value", out _));
        Assert.Equal("Missing", sensors[1].GetProperty("Quality").GetString());
    }

    [Fact]
    public void Csv_escapes_and_leaves_missing_blank()
    {
        var lines = _svc.Render(Sample(), ReportFormat.Csv).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"Package, \"\"hot\"\"\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("Vcore,Voltage,,,,,V", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Text_respects_section_selection()
    {
        var text = _svc.Render(Sample() with { Sections = ReportSections.Summary }, ReportFormat.Text);
        Assert.Contains("SYSTEM SUMMARY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SENSORS", text, StringComparison.Ordinal);
        Assert.Contains("8 / 16", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.2345, SensorUnit.Volt, "1.235 V")]
    [InlineData(4321.7, SensorUnit.Megahertz, "4322 MHz")]
    [InlineData(null, SensorUnit.Celsius, "N/A")]
    [InlineData(0.5, SensorUnit.None, "0.5")]
    [InlineData(61.25, SensorUnit.Celsius, "61.3 °C")]
    public void Formats_values_with_units(double? value, SensorUnit unit, string expected) =>
        Assert.Equal(expected, ReportExportService.FormatValue(value, unit));

    [Fact]
    public async Task Export_writes_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nova-{Guid.NewGuid():N}", "r.html");
        await _svc.ExportAsync(Sample(), ReportFormat.Html, path, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}
