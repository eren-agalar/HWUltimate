using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaHardware.Core.Abstractions;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Reports;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Reports;

/// <summary>Renders hardware reports to HTML, JSON, plain text and CSV. Missing values are written as "N/A".</summary>
public sealed class ReportExportService : IReportExportService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<ReportFormat> SupportedFormats { get; } = [ReportFormat.Html, ReportFormat.Json, ReportFormat.Text, ReportFormat.Csv];

    public static string FileExtension(ReportFormat format) => format switch
    {
        ReportFormat.Html => ".html",
        ReportFormat.Json => ".json",
        ReportFormat.Text => ".txt",
        ReportFormat.Csv => ".csv",
        _ => ".txt",
    };

    public async Task ExportAsync(ReportData data, ReportFormat format, string path, CancellationToken cancellationToken = default)
    {
        var content = Render(data, format);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ReportFormat.Csv), cancellationToken).ConfigureAwait(false);
    }

    public string Render(ReportData data, ReportFormat format) => format switch
    {
        ReportFormat.Html => RenderHtml(data),
        ReportFormat.Json => RenderJson(data),
        ReportFormat.Text => RenderText(data),
        ReportFormat.Csv => RenderCsv(data),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    internal static string FormatValue(double? value, SensorUnit unit) => unit.FormatValue(value, Inv);

    private static string Bytes(ulong? bytes) => bytes is { } b ? $"{b / 1024d / 1024 / 1024:0.0} GB" : "N/A";

    private static IEnumerable<(string Key, string? Value)> SummaryRows(SystemSummary s) =>
    [
        ("Computer", s.ComputerName),
        ("Operating system", s.OperatingSystem),
        ("OS version", s.OsVersion is null ? null : $"{s.OsVersion} (build {s.OsBuild})"),
        ("System", Join(s.SystemManufacturer, s.SystemModel)),
        ("Motherboard", s.Motherboard),
        ("BIOS", Join(s.BiosVersion, s.BiosDate)),
        ("Processor", s.Processor),
        ("Cores / threads", s.PhysicalCores is null ? null : $"{s.PhysicalCores} / {s.LogicalProcessors}"),
        ("Installed memory", s.TotalMemoryBytes is null ? null : Bytes(s.TotalMemoryBytes)),
        ("Graphics", s.Gpus.Count == 0 ? null : string.Join("; ", s.Gpus)),
        ("Uptime", s.Uptime is { } u ? $"{(int)u.TotalDays}d {u.Hours}h {u.Minutes}m" : null),
    ];

    private static string? Join(string? a, string? b) =>
        string.Join(" ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : null;

    private static string DeviceName(ReportData data, string deviceId) =>
        data.Sensors.Devices.FirstOrDefault(d => d.DeviceId == deviceId)?.Name ?? deviceId;

    // ---------------------------------------------------------------- JSON
    private static string RenderJson(ReportData d)
    {
        var doc = new
        {
            product = "Nova Hardware",
            version = d.ApplicationVersion,
            generatedAt = d.GeneratedAt,
            summary = d.Sections.HasFlag(ReportSections.Summary) ? d.Inventory.Summary : null,
            devices = d.Sections.HasFlag(ReportSections.Devices) ? d.Inventory.Devices : null,
            sensors = d.Sections.HasFlag(ReportSections.Sensors)
                ? d.Sensors.Sensors.Select(s => new
                {
                    s.SensorId,
                    device = DeviceName(d, s.DeviceId),
                    s.Name,
                    s.Type,
                    unit = s.UnitSymbol,
                    s.Value,
                    s.Minimum,
                    s.Maximum,
                    s.Average,
                    s.SampleCount,
                    provider = s.ProviderName,
                    s.Availability,
                    s.Quality,
                    s.Timestamp,
                })
                : null,
            alerts = d.Sections.HasFlag(ReportSections.Alerts) ? d.Alerts : null,
            stressTests = d.Sections.HasFlag(ReportSections.StressTests) ? d.StressTests : null,
            warnings = d.Inventory.Warnings.Count > 0 ? d.Inventory.Warnings : null,
        };
        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    // ---------------------------------------------------------------- CSV
    private static string RenderCsv(ReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Device,Sensor,Type,Value,Minimum,Maximum,Average,Unit,Provider,Quality,Timestamp,SensorId");
        foreach (var s in d.Sensors.Sensors)
        {
            sb.AppendJoin(',',
                Csv(DeviceName(d, s.DeviceId)), Csv(s.Name), s.Type,
                Num(s.Value), Num(s.Minimum), Num(s.Maximum), Num(s.Average),
                Csv(s.UnitSymbol), Csv(s.ProviderName), s.Quality,
                s.Timestamp.ToString("O", Inv), Csv(s.SensorId));
            sb.AppendLine();
        }

        return sb.ToString();

        static string Num(double? v) => v?.ToString("R", Inv) ?? string.Empty;
    }

    internal static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    // ---------------------------------------------------------------- TEXT
    private static string RenderText(ReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NOVA HARDWARE — SYSTEM REPORT");
        sb.AppendLine($"Generated: {d.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}    Version: {d.ApplicationVersion}");
        sb.AppendLine(new string('=', 78));

        if (d.Sections.HasFlag(ReportSections.Summary))
        {
            Section(sb, "SYSTEM SUMMARY");
            foreach (var (k, v) in SummaryRows(d.Inventory.Summary))
            {
                sb.AppendLine($"  {k,-20} {v ?? "N/A"}");
            }
        }

        if (d.Sections.HasFlag(ReportSections.Devices))
        {
            Section(sb, "DEVICES");
            foreach (var group in d.Inventory.Devices.GroupBy(x => x.Category).OrderBy(g => g.Key))
            {
                sb.AppendLine($"  [{group.Key}]");
                foreach (var dev in group)
                {
                    sb.AppendLine($"    {dev.Name}");
                    foreach (var p in dev.Properties)
                    {
                        sb.AppendLine($"      {p.Name,-28} {p.Value}");
                    }
                }
            }
        }

        if (d.Sections.HasFlag(ReportSections.Sensors))
        {
            Section(sb, "SENSORS");
            foreach (var group in d.Sensors.Sensors.GroupBy(s => s.DeviceId))
            {
                sb.AppendLine($"  {DeviceName(d, group.Key)}");
                foreach (var s in group)
                {
                    sb.AppendLine($"    {s.Name,-32} {FormatValue(s.Value, s.Unit),14}  min {FormatValue(s.Minimum, s.Unit),12}  max {FormatValue(s.Maximum, s.Unit),12}  avg {FormatValue(s.Average, s.Unit),12}");
                }
            }
        }

        if (d.Sections.HasFlag(ReportSections.Alerts))
        {
            Section(sb, "ALERTS");
            if (d.Alerts.Count == 0) sb.AppendLine("  No alerts recorded.");
            foreach (var a in d.Alerts)
            {
                sb.AppendLine($"  {a.Timestamp:yyyy-MM-dd HH:mm:ss} [{a.Severity}] {a.RuleName}: {a.Message}");
            }
        }

        if (d.Sections.HasFlag(ReportSections.StressTests))
        {
            Section(sb, "STABILITY TESTS");
            if (d.StressTests.Count == 0) sb.AppendLine("  No stability tests run in this session.");
            foreach (var t in d.StressTests)
            {
                sb.AppendLine($"  {t.StartedAt:yyyy-MM-dd HH:mm} {t.Kind,-18} {t.Outcome,-15} {t.Summary}");
            }
        }

        return sb.ToString();

        static void Section(StringBuilder sb, string title)
        {
            sb.AppendLine();
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
        }
    }

    // ---------------------------------------------------------------- HTML
    /// <summary>Escapes markup-significant characters only; the document is UTF-8 so symbols like °C stay readable.</summary>
    internal static string H(string? s)
    {
        s ??= "N/A";
        if (s.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    private static string RenderHtml(ReportData d)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Nova Hardware Report</title>
            <style>
            :root{--bg:#0b0e14;--panel:#121722;--line:#232b3a;--text:#e6ebf5;--muted:#8a96ad;--accent:#5b8cff;--ok:#3ccf91;--bad:#ff5d6c;--warn:#ffb547}
            @media (prefers-color-scheme: light){:root{--bg:#f5f7fb;--panel:#fff;--line:#e2e7f0;--text:#141a26;--muted:#5c6880}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
            main{max-width:1100px;margin:0 auto;padding:32px 16px}
            header h1{margin:0;font-size:28px;letter-spacing:.08em}header p{margin:4px 0 0;color:var(--muted)}
            .accent{color:var(--accent)}section{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:20px;margin-top:20px}
            h2{margin:0 0 12px;font-size:16px;text-transform:uppercase;letter-spacing:.1em;color:var(--muted)}
            h3{margin:18px 0 8px;font-size:15px}table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
            th,td{padding:6px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{color:var(--muted);font-weight:600}
            td.n{text-align:right;white-space:nowrap}.wrap{overflow-x:auto}.tag{display:inline-block;padding:1px 8px;border-radius:99px;font-size:12px;border:1px solid var(--line)}
            .Passed{color:var(--ok)}.Failed,.Critical,.AbortedThermal{color:var(--bad)}.Warning{color:var(--warn)}.muted{color:var(--muted)}
            </style></head><body><main>
            """);
        sb.Append(CultureInfo.InvariantCulture, $"<header><h1>NOVA <span class=\"accent\">HARDWARE</span></h1><p>System report · generated {H(d.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv))} · v{H(d.ApplicationVersion)}</p></header>");

        if (d.Sections.HasFlag(ReportSections.Summary))
        {
            sb.Append("<section><h2>System summary</h2><table>");
            foreach (var (k, v) in SummaryRows(d.Inventory.Summary))
            {
                sb.Append(Inv, $"<tr><th>{H(k)}</th><td>{H(v)}</td></tr>");
            }

            sb.Append("</table></section>");
        }

        if (d.Sections.HasFlag(ReportSections.Devices))
        {
            sb.Append("<section><h2>Devices</h2>");
            foreach (var group in d.Inventory.Devices.GroupBy(x => x.Category).OrderBy(g => g.Key))
            {
                foreach (var dev in group)
                {
                    sb.Append(Inv, $"<h3>{H(dev.Name)} <span class=\"tag\">{H(group.Key.ToString())}</span></h3>");
                    if (dev.Properties.Count > 0)
                    {
                        sb.Append("<div class=\"wrap\"><table>");
                        foreach (var p in dev.Properties)
                        {
                            sb.Append(Inv, $"<tr><th>{H(p.Name)}</th><td>{H(p.Value)}</td></tr>");
                        }

                        sb.Append("</table></div>");
                    }
                }
            }

            sb.Append("</section>");
        }

        if (d.Sections.HasFlag(ReportSections.Sensors))
        {
            sb.Append("<section><h2>Sensors</h2>");
            foreach (var group in d.Sensors.Sensors.GroupBy(s => s.DeviceId))
            {
                sb.Append(Inv, $"<h3>{H(DeviceName(d, group.Key))}</h3><div class=\"wrap\"><table><tr><th>Sensor</th><th>Type</th><th>Current</th><th>Min</th><th>Max</th><th>Avg</th><th>Source</th></tr>");
                foreach (var s in group)
                {
                    sb.Append(Inv, $"<tr><td>{H(s.Name)}</td><td class=\"muted\">{H(s.Type.ToString())}</td><td class=\"n\">{H(FormatValue(s.Value, s.Unit))}</td><td class=\"n\">{H(FormatValue(s.Minimum, s.Unit))}</td><td class=\"n\">{H(FormatValue(s.Maximum, s.Unit))}</td><td class=\"n\">{H(FormatValue(s.Average, s.Unit))}</td><td class=\"muted\">{H(s.ProviderName)}</td></tr>");
                }

                sb.Append("</table></div>");
            }

            sb.Append("</section>");
        }

        if (d.Sections.HasFlag(ReportSections.Alerts))
        {
            sb.Append("<section><h2>Alerts</h2>");
            if (d.Alerts.Count == 0)
            {
                sb.Append("<p class=\"muted\">No alerts recorded.</p>");
            }
            else
            {
                sb.Append("<div class=\"wrap\"><table><tr><th>Time</th><th>Severity</th><th>Rule</th><th>Detail</th></tr>");
                foreach (var a in d.Alerts)
                {
                    sb.Append(Inv, $"<tr><td>{H(a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", Inv))}</td><td class=\"{a.Severity}\">{a.Severity}</td><td>{H(a.RuleName)}</td><td>{H(a.Message)}</td></tr>");
                }

                sb.Append("</table></div>");
            }

            sb.Append("</section>");
        }

        if (d.Sections.HasFlag(ReportSections.StressTests))
        {
            sb.Append("<section><h2>Stability tests</h2>");
            if (d.StressTests.Count == 0)
            {
                sb.Append("<p class=\"muted\">No stability tests run in this session.</p>");
            }
            else
            {
                sb.Append("<div class=\"wrap\"><table><tr><th>Started</th><th>Test</th><th>Result</th><th>Duration</th><th>Iterations</th><th>Errors</th><th>Max temp</th></tr>");
                foreach (var t in d.StressTests)
                {
                    sb.Append(Inv, $"<tr><td>{H(t.StartedAt.ToString("yyyy-MM-dd HH:mm", Inv))}</td><td>{t.Kind}</td><td class=\"{t.Outcome}\">{t.Outcome}</td><td class=\"n\">{t.Elapsed:hh\\:mm\\:ss}</td><td class=\"n\">{t.Iterations.ToString("N0", Inv)}</td><td class=\"n\">{t.Errors.ToString("N0", Inv)}</td><td class=\"n\">{H(FormatValue(t.MaxTemperature, SensorUnit.Celsius))}</td></tr>");
                }

                sb.Append("</table></div>");
            }

            sb.Append("</section>");
        }

        if (d.Inventory.Warnings.Count > 0)
        {
            sb.Append("<section><h2>Collection notes</h2><ul>");
            foreach (var w in d.Inventory.Warnings)
            {
                sb.Append(Inv, $"<li class=\"muted\">{H(w)}</li>");
            }

            sb.Append("</ul></section>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
