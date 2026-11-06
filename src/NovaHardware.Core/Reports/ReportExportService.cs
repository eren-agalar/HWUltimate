using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.HardwareInfo;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Reports;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Core.Reports;

/// <summary>One row of the in-app report preview.</summary>
public sealed record PreviewRow(string Label, string Value, string? Detail = null, bool IsAvailable = true);

/// <summary>A titled block of the in-app report preview (optionally one per device).</summary>
public sealed record PreviewSection(string Title, string? Subtitle, IReadOnlyList<PreviewRow> Rows);

/// <summary>
/// Renders Nova hardware reports (HTML, JSON, CSV, plain text) and a structured preview of the same content.
/// Unavailable values are written as "N/A" / "Not Available"; nothing is estimated.
/// </summary>
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

    public static IReadOnlyList<(ReportSections Section, HardwareCategory[] Categories, string Title)> ComponentSections { get; } =
    [
        (ReportSections.Cpu, [HardwareCategory.Cpu], "Processor"),
        (ReportSections.Gpu, [HardwareCategory.Gpu], "Graphics"),
        (ReportSections.Memory, [HardwareCategory.Memory], "Memory"),
        (ReportSections.Storage, [HardwareCategory.Storage], "Storage"),
    ];

    public IReadOnlyList<ReportFormat> SupportedFormats { get; } = [ReportFormat.Html, ReportFormat.Csv, ReportFormat.Json, ReportFormat.Text];

    public static string FileExtension(ReportFormat format) => format switch
    {
        ReportFormat.Html => ".html",
        ReportFormat.Json => ".json",
        ReportFormat.Csv => ".csv",
        _ => ".txt",
    };

    /// <summary>Writes the report, reads it back and validates it. Throws <see cref="InvalidDataException"/> if validation fails.</summary>
    public async Task ExportAsync(ReportData data, ReportFormat format, string path, CancellationToken cancellationToken = default)
    {
        var content = Render(data, format);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ReportFormat.Csv), cancellationToken).ConfigureAwait(false);

        var written = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var (ok, error) = ReportValidator.Validate(format, written);
        if (!ok) throw new InvalidDataException($"The exported {format} file failed validation: {error}");
    }

    public string Render(ReportData data, ReportFormat format) => format switch
    {
        ReportFormat.Html => RenderHtml(data),
        ReportFormat.Json => RenderJson(data),
        ReportFormat.Csv => RenderCsv(data),
        ReportFormat.Text => RenderText(data),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    internal static string FormatValue(double? value, SensorUnit unit) => unit.FormatValue(value, Inv);

    public static IEnumerable<(string Key, string? Value)> SummaryRows(SystemSummary s) =>
    [
        ("Computer", s.ComputerName),
        ("Operating system", s.OperatingSystem),
        ("OS version", s.OsVersion is null ? null : $"{s.OsVersion} (build {s.OsBuild})"),
        ("System", Join(s.SystemManufacturer, s.SystemModel)),
        ("Motherboard", s.Motherboard),
        ("BIOS", Join(s.BiosVersion, s.BiosDate)),
        ("Processor", s.Processor),
        ("Cores / threads", s.PhysicalCores is null ? null : $"{s.PhysicalCores} / {s.LogicalProcessors}"),
        ("Installed memory", s.TotalMemoryBytes is { } b ? $"{b / 1024d / 1024 / 1024:0.0} GB" : null),
        ("Graphics", s.Gpus.Count == 0 ? null : string.Join("; ", s.Gpus)),
        ("Uptime", s.Uptime is { } u ? $"{(int)u.TotalDays}d {u.Hours}h {u.Minutes}m" : null),
    ];

    private static string? Join(string? a, string? b) =>
        string.Join(" ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } s ? s : null;

    private static string DeviceName(ReportData data, string deviceId) =>
        data.Sensors.Devices.FirstOrDefault(d => d.DeviceId == deviceId)?.Name ?? deviceId;

    private static IEnumerable<SensorReading> ReportSensors(ReportData d) => d.Sensors.Sensors;

    private static IEnumerable<(string Title, IReadOnlyList<DeviceInfo> Devices)> Components(ReportData d) =>
        ComponentSections.Where(c => d.Sections.HasFlag(c.Section))
            .Select(c => (c.Title, (IReadOnlyList<DeviceInfo>)d.Inventory.Devices.Where(x => c.Categories.Contains(x.Category)).ToList()));

    // ================================================================ preview
    /// <summary>Structured preview of exactly the content that will be exported, for native rendering in the app.</summary>
    public static IReadOnlyList<PreviewSection> BuildPreview(ReportData d)
    {
        var list = new List<PreviewSection>();
        if (d.Sections.HasFlag(ReportSections.Summary))
        {
            list.Add(new PreviewSection("System summary", null,
                SummaryRows(d.Inventory.Summary).Select(r => new PreviewRow(r.Key, r.Value ?? "N/A", IsAvailable: r.Value is not null)).ToList()));
        }

        if (d.Sections.HasFlag(ReportSections.Inventory))
        {
            list.Add(new PreviewSection("Hardware inventory", $"{d.Inventory.Devices.Count} devices",
                d.Inventory.Devices.Select(x => new PreviewRow(x.Name, x.Category.ToString(), x.Source)).ToList()));
        }

        foreach (var (title, devices) in Components(d))
        {
            if (devices.Count == 0)
            {
                list.Add(new PreviewSection(title, null, [new PreviewRow("Devices", "None detected", IsAvailable: false)]));
                continue;
            }

            foreach (var dev in devices)
            {
                list.Add(new PreviewSection($"{title} — {dev.Name}", dev.Source,
                    dev.Properties.Select(p => new PreviewRow(p.Name, HardwareInfoExporter.FormatValue(p), p.Source, p.IsAvailable)).ToList()));
            }
        }

        if (d.Sections.HasFlag(ReportSections.Sensors))
        {
            foreach (var g in ReportSensors(d).GroupBy(s => s.DeviceId))
            {
                list.Add(new PreviewSection($"Sensors — {DeviceName(d, g.Key)}", null,
                    g.Select(s => new PreviewRow(s.Name, FormatValue(s.Value, s.Unit),
                        $"min {FormatValue(s.Minimum, s.Unit)} · max {FormatValue(s.Maximum, s.Unit)} · avg {FormatValue(s.Average, s.Unit)} · {s.ProviderName}", s.Value.HasValue)).ToList()));
            }
        }

        if (d.Sections.HasFlag(ReportSections.Alerts))
        {
            list.Add(new PreviewSection("Alert history", $"{d.Alerts.Count} events",
                d.Alerts.Count == 0
                    ? [new PreviewRow("Alerts", "No alerts recorded", IsAvailable: false)]
                    : d.Alerts.Select(a => new PreviewRow(a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", Inv), a.Message, $"{a.Severity} · {a.RuleName}")).ToList()));
        }

        return list;
    }

    // ================================================================ JSON
    private static string RenderJson(ReportData d)
    {
        var doc = new
        {
            product = "Nova Hardware",
            version = d.ApplicationVersion,
            generatedAt = d.GeneratedAt,
            summary = d.Sections.HasFlag(ReportSections.Summary) ? d.Inventory.Summary : null,
            inventory = d.Sections.HasFlag(ReportSections.Inventory)
                ? d.Inventory.Devices.Select(x => new { x.Name, x.Category, x.Manufacturer, x.Model, x.Source })
                : null,
            components = Components(d).ToDictionary(c => c.Title, c => c.Devices),
            sensors = d.Sections.HasFlag(ReportSections.Sensors)
                ? ReportSensors(d).Select(s => new
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
                    s.Kind,
                    s.Timestamp,
                })
                : null,
            alerts = d.Sections.HasFlag(ReportSections.Alerts) ? d.Alerts : null,
            warnings = d.Inventory.Warnings.Count > 0 ? d.Inventory.Warnings : null,
        };
        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    // ================================================================ CSV
    public const string CsvHeader = "Section,Device,Group,Name,Value,Minimum,Maximum,Average,Unit,Source,Status";

    private static string RenderCsv(ReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CsvHeader);
        void Row(params string?[] cells) => sb.AppendLine(string.Join(',', cells.Select(c => Csv(c ?? string.Empty))));
        static string Num(double? v) => v?.ToString("R", Inv) ?? string.Empty;

        if (d.Sections.HasFlag(ReportSections.Summary))
        {
            foreach (var (k, v) in SummaryRows(d.Inventory.Summary)) Row("Summary", "", "", k, v ?? "N/A", "", "", "", "", "WMI", v is null ? "NotAvailable" : "Available");
        }

        if (d.Sections.HasFlag(ReportSections.Inventory))
        {
            foreach (var x in d.Inventory.Devices) Row("Inventory", x.Name, x.Category.ToString(), "Device", x.Model ?? x.Name, "", "", "", "", x.Source, "Available");
        }

        foreach (var (title, devices) in Components(d))
        {
            foreach (var dev in devices)
                foreach (var p in dev.Properties)
                    Row(title, dev.Name, HardwareInfoExporter.SectionOf(p), p.Name, HardwareInfoExporter.FormatValue(p), "", "", "", "", p.Source, p.Status.ToString());
        }

        if (d.Sections.HasFlag(ReportSections.Sensors))
        {
            foreach (var s in ReportSensors(d))
            {
                Row("Sensors", DeviceName(d, s.DeviceId), s.Type.ToString(), s.Name, Num(s.Value), Num(s.Minimum), Num(s.Maximum), Num(s.Average), s.UnitSymbol, s.ProviderName, s.Quality.ToString());
            }
        }

        if (d.Sections.HasFlag(ReportSections.Alerts))
        {
            foreach (var a in d.Alerts) Row("Alerts", a.DeviceName, a.Severity.ToString(), a.RuleName, a.Message, "", "", "", a.Unit.ToSymbol(), a.Kind.ToString(), a.State.ToString());
        }

        return sb.ToString();
    }

    public static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    // ================================================================ TEXT
    private static string RenderText(ReportData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NOVA HARDWARE — SYSTEM REPORT");
        sb.AppendLine($"Generated: {d.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}    Version: {d.ApplicationVersion}");
        sb.AppendLine(new string('=', 78));
        foreach (var section in BuildPreview(d))
        {
            sb.AppendLine();
            sb.AppendLine(section.Title.ToUpperInvariant() + (section.Subtitle is null ? "" : $"  ({section.Subtitle})"));
            sb.AppendLine(new string('-', Math.Min(78, section.Title.Length)));
            foreach (var r in section.Rows)
            {
                sb.Append("  ").Append(r.Label.PadRight(34)).Append(' ').Append(r.Value);
                if (r.Detail is not null) sb.Append("   [").Append(r.Detail).Append(']');
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // ================================================================ HTML
    /// <summary>Escapes markup-significant characters only; the document is UTF-8 so symbols like °C stay readable.</summary>
    public static string H(string? s)
    {
        s ??= "N/A";
        if (s.AsSpan().IndexOfAny("&<>\"'") < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => c.ToString() });
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
            :root{--bg:#11151d;--panel:#191f2a;--surface:#202838;--line:#2a3443;--text:#f3f5f8;--muted:#929dae;--accent:#6595ff;--ok:#36c991;--warn:#e9ac4b;--bad:#e45d65}
            @media (prefers-color-scheme: light){:root{--bg:#f4f6f9;--panel:#fff;--surface:#f0f3f7;--line:#dfe4eb;--text:#151a22;--muted:#5d6878;--accent:#2f6bff}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
            main{max-width:1120px;margin:0 auto;padding:40px 16px}
            header{display:flex;justify-content:space-between;align-items:flex-end;flex-wrap:wrap;gap:8px;border-bottom:1px solid var(--line);padding-bottom:18px}
            header h1{margin:0;font-size:26px;letter-spacing:.12em;font-weight:600}header h1 span{color:var(--accent)}
            header p{margin:0;color:var(--muted);font-size:13px}
            section{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin-top:18px}
            h2{margin:0 0 2px;font-size:15px;font-weight:600}.sub{color:var(--muted);font-size:12px;margin-bottom:10px}
            table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
            th,td{padding:6px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}
            th{color:var(--muted);font-weight:500;width:34%}td.n{text-align:right;white-space:nowrap}
            tr:last-child td,tr:last-child th{border-bottom:0}.na{color:var(--muted);font-style:italic}.src{color:var(--muted);font-size:12px}
            .wrap{overflow-x:auto}.Warning{color:var(--warn)}.Critical{color:var(--bad)}.Information{color:var(--accent)}
            footer{color:var(--muted);font-size:12px;margin-top:28px;text-align:center}
            </style></head><body><main>
            """);
        sb.Append(Inv, $"<header><h1>NOVA <span>HARDWARE</span></h1><p>System report · {H(d.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv))} · v{H(d.ApplicationVersion)}</p></header>");

        foreach (var section in BuildPreview(d))
        {
            var isSensors = section.Title.StartsWith("Sensors — ", StringComparison.Ordinal);
            sb.Append(Inv, $"<section><h2>{H(section.Title)}</h2>");
            if (section.Subtitle is not null) sb.Append(Inv, $"<div class=\"sub\">{H(section.Subtitle)}</div>");
            sb.Append("<div class=\"wrap\"><table>");
            foreach (var r in section.Rows)
            {
                var cls = r.IsAvailable ? (isSensors ? " class=\"n\"" : "") : " class=\"na\"";
                var detail = r.Detail is null ? "" : $"<div class=\"src\">{H(r.Detail)}</div>";
                sb.Append(Inv, $"<tr><th>{H(r.Label)}</th><td{cls}>{H(r.Value)}{detail}</td></tr>");
            }

            sb.Append("</table></div></section>");
        }

        if (d.Inventory.Warnings.Count > 0)
        {
            sb.Append("<section><h2>Collection notes</h2><ul>");
            foreach (var w in d.Inventory.Warnings) sb.Append(Inv, $"<li class=\"src\">{H(w)}</li>");
            sb.Append("</ul></section>");
        }

        sb.Append("<footer>Generated by Nova Hardware. Values marked Not Available were not exposed by the system; nothing is estimated.</footer></main></body></html>");
        return sb.ToString();
    }
}

/// <summary>Checks that an exported report is well-formed before success is reported to the user.</summary>
public static class ReportValidator
{
    public static (bool Ok, string? Error) Validate(ReportFormat format, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (false, "file is empty");
        switch (format)
        {
            case ReportFormat.Json:
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    return doc.RootElement.TryGetProperty("product", out _) ? (true, null) : (false, "missing product header");
                }
                catch (JsonException ex)
                {
                    return (false, ex.Message);
                }

            case ReportFormat.Html:
                return content.Contains("<html", StringComparison.OrdinalIgnoreCase) && content.TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase)
                    ? (true, null) : (false, "HTML document is incomplete");

            case ReportFormat.Csv:
                var lines = SplitCsvRecords(content.TrimStart('﻿'));
                if (lines.Count == 0 || lines[0] != ReportExportService.CsvHeader) return (false, "missing CSV header");
                var expected = CountFields(lines[0]);
                for (var i = 1; i < lines.Count; i++)
                {
                    if (CountFields(lines[i]) != expected) return (false, $"row {i + 1} has the wrong number of columns");
                }

                return (true, null);

            default:
                return content.StartsWith("NOVA HARDWARE", StringComparison.Ordinal) ? (true, null) : (false, "missing report header");
        }
    }

    /// <summary>Splits CSV into records, honouring quoted newlines.</summary>
    internal static List<string> SplitCsvRecords(string content)
    {
        var records = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '"') quoted = !quoted;
            if (!quoted && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                if (sb.Length > 0) records.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0) records.Add(sb.ToString());
        return records;
    }

    internal static int CountFields(string record)
    {
        var count = 1;
        var quoted = false;
        foreach (var c in record)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) count++;
        }

        return count;
    }
}
