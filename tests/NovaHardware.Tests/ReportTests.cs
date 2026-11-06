using System.Text.Json;
using NovaHardware.Core.Reports;
using NovaHardware.Domain.Alerts;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Reports;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Tests;

public class ReportTests
{
    internal static ReportData Sample()
    {
        var devices = new[] { new HardwareDevice("cpu", "Test <CPU>", HardwareCategory.Cpu, "lhm") };
        var snap = Snapshots.Of(DateTimeOffset.Now, devices,
            Snapshots.Reading("t", "cpu", "Package, \"hot\"", SensorType.Temperature, 61.25) with { Minimum = 40, Maximum = 70, Average = 55 },
            Snapshots.Reading("v", "cpu", "Vcore", SensorType.Voltage, null, DataQuality.Missing));
        var inventory = new HardwareInventory(DateTimeOffset.Now, new SystemSummary { Processor = "Test CPU", PhysicalCores = 8, LogicalProcessors = 16 },
        [
            new DeviceInfo { Id = "c", Name = "Test CPU", Category = HardwareCategory.Cpu, Source = "CPUID", Properties = [new("Codename", "Raphael", PropertySections.Identification, "CPUID"), HardwareProperty.Unavailable("TDP", PropertySections.Specifications, "not exposed")] },
            new DeviceInfo { Id = "g", Name = "RTX 4090", Category = HardwareCategory.Gpu, Source = "PCI", Properties = [new("VBIOS version", "95.02", PropertySections.Firmware, "NVML")] },
            new DeviceInfo { Id = "b", Name = "Board", Category = HardwareCategory.Motherboard, Source = "SMBIOS", Properties = [new("BIOS", "1.0")] },
        ], []);
        var alert = new AlertEvent(Guid.NewGuid(), "CPU temperature", AlertSeverity.Warning, AlertState.Raised, AlertKind.WarningThreshold, "t", "Package", "Test CPU", 88, 85, SensorUnit.Celsius, DateTimeOffset.Now, "auto: AMD default");
        return new ReportData { ApplicationVersion = "0.2.0", Sensors = snap, Inventory = inventory, Alerts = [alert] };
    }

    private readonly ReportExportService _svc = new();

    [Fact]
    public void Preview_is_never_blank_when_data_exists_and_follows_selection()
    {
        var all = ReportExportService.BuildPreview(Sample());
        Assert.Contains(all, s => s.Title == "System summary");
        Assert.Contains(all, s => s.Title == "Hardware inventory" && s.Rows.Count == 3);
        Assert.Contains(all, s => s.Title == "Processor — Test CPU" && s.Rows.Any(r => r.Value == "Not Available" && !r.IsAvailable));
        Assert.Contains(all, s => s.Title == "Graphics — RTX 4090");
        Assert.Contains(all, s => s.Title == "Memory" && s.Rows.Single().Value == "None detected");
        Assert.Contains(all, s => s.Title.StartsWith("Sensors — ", StringComparison.Ordinal) && s.Rows.Count == 2);
        Assert.Contains(all, s => s.Title == "Alert history" && s.Rows.Single().Value.Contains("warning threshold", StringComparison.Ordinal));

        var onlyGpu = ReportExportService.BuildPreview(Sample() with { Sections = ReportSections.Gpu });
        Assert.Equal("Graphics — RTX 4090", Assert.Single(onlyGpu).Title);
        Assert.Empty(ReportExportService.BuildPreview(Sample() with { Sections = ReportSections.None }));
    }

    [Fact]
    public void Html_is_encoded_valid_and_shows_unavailable()
    {
        var html = _svc.Render(Sample(), ReportFormat.Html);
        Assert.Contains("Test &lt;CPU&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<CPU>", html, StringComparison.Ordinal);
        Assert.Contains("61.3 °C", html, StringComparison.Ordinal);
        Assert.Contains("class=\"na\"", html, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme", html, StringComparison.Ordinal);
        Assert.True(ReportValidator.Validate(ReportFormat.Html, html).Ok);
    }

    [Fact]
    public void Json_is_valid_and_structured()
    {
        var json = _svc.Render(Sample(), ReportFormat.Json);
        Assert.True(ReportValidator.Validate(ReportFormat.Json, json).Ok);
        using var doc = JsonDocument.Parse(json);
        var sensors = doc.RootElement.GetProperty("sensors");
        Assert.Equal(2, sensors.GetArrayLength());
        Assert.Equal("°C", sensors[0].GetProperty("unit").GetString());
        Assert.False(sensors[1].TryGetProperty("Value", out _));
        Assert.Equal("Measurement", sensors[0].GetProperty("Kind").GetString());
        Assert.True(doc.RootElement.GetProperty("components").TryGetProperty("Processor", out _));
        Assert.Equal("WarningThreshold", doc.RootElement.GetProperty("alerts")[0].GetProperty("Kind").GetString());
    }

    [Fact]
    public void Csv_is_rectangular_escaped_and_validated()
    {
        var csv = _svc.Render(Sample(), ReportFormat.Csv);
        Assert.True(ReportValidator.Validate(ReportFormat.Csv, csv).Ok);
        Assert.StartsWith(ReportExportService.CsvHeader, csv, StringComparison.Ordinal);
        Assert.Contains("\"Package, \"\"hot\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("Sensors,Test <CPU>,Voltage,Vcore,,,,,V", csv, StringComparison.Ordinal);
        Assert.Contains("Processor,Test CPU,Technical Specifications,TDP,Not Available", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReportFormat.Json, "{ broken")]
    [InlineData(ReportFormat.Html, "<html><body>")]
    [InlineData(ReportFormat.Csv, "Wrong,Header\n1,2")]
    [InlineData(ReportFormat.Csv, ReportExportService.CsvHeader + "\na,b")]
    [InlineData(ReportFormat.Text, "")]
    public void Validator_rejects_malformed_files(ReportFormat format, string content) =>
        Assert.False(ReportValidator.Validate(format, content).Ok);

    [Fact]
    public void Csv_validator_handles_quoted_newlines()
    {
        var csv = ReportExportService.CsvHeader + "\nSensors,dev,g,\"multi\nline\",1,,,,,src,Good\n";
        Assert.True(ReportValidator.Validate(ReportFormat.Csv, csv).Ok);
    }

    [Theory]
    [InlineData(1.2345, SensorUnit.Volt, "1.235 V")]
    [InlineData(4321.7, SensorUnit.Megahertz, "4322 MHz")]
    [InlineData(null, SensorUnit.Celsius, "N/A")]
    [InlineData(0.5, SensorUnit.None, "0.5")]
    [InlineData(61.25, SensorUnit.Celsius, "61.3 °C")]
    public void Formats_values_with_units(double? value, SensorUnit unit, string expected) =>
        Assert.Equal(expected, ReportExportService.FormatValue(value, unit));

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Csv)]
    [InlineData(ReportFormat.Json)]
    [InlineData(ReportFormat.Text)]
    public async Task Export_writes_and_validates_file(ReportFormat format)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nova-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "r" + ReportExportService.FileExtension(format));
        await _svc.ExportAsync(Sample(), format, path, TestContext.Current.CancellationToken);
        Assert.True(new FileInfo(path).Length > 100);
        Directory.Delete(dir, true);
    }
}
