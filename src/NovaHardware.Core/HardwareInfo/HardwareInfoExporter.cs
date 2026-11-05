using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaHardware.Core.Reports;
using NovaHardware.Domain.Hardware;

namespace NovaHardware.Core.HardwareInfo;

public enum HardwareInfoFormat { Text = 0, Json, Html }

public sealed record PropertyMatch(DeviceInfo Device, HardwareProperty Property);

/// <summary>Copy / export / search over the hardware inventory. Unavailable properties are kept and labelled.</summary>
public static class HardwareInfoExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Section of a property; properties without a section go to Overview.</summary>
    public static string SectionOf(HardwareProperty p) => string.IsNullOrWhiteSpace(p.Group) ? PropertySections.Overview : p.Group;

    public static IEnumerable<(string Section, IReadOnlyList<HardwareProperty> Properties)> Sections(DeviceInfo device)
    {
        var groups = device.Properties.GroupBy(SectionOf).ToDictionary(g => g.Key, g => (IReadOnlyList<HardwareProperty>)g.ToList());
        foreach (var s in PropertySections.All.Concat(groups.Keys.Except(PropertySections.All)))
        {
            if (groups.TryGetValue(s, out var props)) yield return (s, props);
        }
    }

    public static string FormatValue(HardwareProperty p) => p.Status switch
    {
        PropertyStatus.Available => p.Value,
        PropertyStatus.RequiresElevation => "Requires administrator rights",
        _ => HardwareProperty.NotAvailableText,
    };

    /// <summary>"Name: value" line used when copying a single property.</summary>
    public static string FormatProperty(HardwareProperty p) => $"{p.Name}: {FormatValue(p)}";

    public static string FormatDevice(DeviceInfo device)
    {
        var sb = new StringBuilder();
        sb.AppendLine(device.Name);
        sb.AppendLine(new string('=', Math.Min(78, Math.Max(8, device.Name.Length))));
        sb.AppendLine($"Category: {device.Category}");
        sb.AppendLine($"Sources:  {device.Source}");
        foreach (var (section, props) in Sections(device))
        {
            sb.AppendLine();
            sb.AppendLine($"[{section}]");
            var width = Math.Min(36, props.Max(p => p.Name.Length));
            foreach (var p in props)
            {
                sb.Append("  ").Append(p.Name.PadRight(width)).Append("  ").Append(FormatValue(p));
                if (!string.IsNullOrEmpty(p.Source)) sb.Append("  (").Append(p.Source).Append(')');
                sb.AppendLine();
                if (!p.IsAvailable && !string.IsNullOrEmpty(p.Note))
                {
                    sb.Append("  ").Append(new string(' ', width)).Append("  ↳ ").AppendLine(p.Note);
                }
            }
        }

        return sb.ToString();
    }

    public static string Export(HardwareInventory inventory, HardwareInfoFormat format, string appVersion = "") => format switch
    {
        HardwareInfoFormat.Text => ExportText(inventory, appVersion),
        HardwareInfoFormat.Json => JsonSerializer.Serialize(new
        {
            product = "Nova Hardware",
            version = appVersion,
            collectedAt = inventory.CollectedAt,
            summary = inventory.Summary,
            devices = inventory.Devices,
            warnings = inventory.Warnings,
        }, Json),
        HardwareInfoFormat.Html => ExportHtml(inventory, appVersion),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string FileExtension(HardwareInfoFormat f) => f switch { HardwareInfoFormat.Json => ".json", HardwareInfoFormat.Html => ".html", _ => ".txt" };

    private static string ExportText(HardwareInventory inv, string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NOVA HARDWARE — HARDWARE INFORMATION");
        sb.AppendLine($"Collected: {inv.CollectedAt:yyyy-MM-dd HH:mm:ss zzz}   Version: {version}");
        sb.AppendLine();
        foreach (var d in inv.Devices)
        {
            sb.AppendLine(FormatDevice(d));
        }

        return sb.ToString();
    }

    private static string ExportHtml(HardwareInventory inv, string version)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Nova Hardware Information</title><style>
            :root{--bg:#0b0e14;--panel:#141924;--line:#232b3a;--text:#e6ebf5;--muted:#8a96ad;--accent:#5b8cff}
            @media (prefers-color-scheme: light){:root{--bg:#f5f7fb;--panel:#fff;--line:#e2e7f0;--text:#141a26;--muted:#5c6880;--accent:#2f6bff}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
            main{max-width:1100px;margin:0 auto;padding:32px 16px}h1{margin:0;letter-spacing:.08em}h1 span{color:var(--accent)}
            section{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:18px;margin-top:18px}
            h2{margin:0 0 4px;font-size:17px}h3{margin:14px 0 6px;font-size:12px;text-transform:uppercase;letter-spacing:.1em;color:var(--muted)}
            table{width:100%;border-collapse:collapse}th,td{padding:5px 8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}
            th{width:34%;color:var(--muted);font-weight:500}.src{color:var(--muted);font-size:12px}.na{color:var(--muted);font-style:italic}.wrap{overflow-x:auto}
            </style></head><body><main>
            """);
        sb.Append($"<h1>NOVA <span>HARDWARE</span></h1><p class=\"src\">Hardware information · collected {H(inv.CollectedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))} · v{H(version)}</p>");
        foreach (var d in inv.Devices)
        {
            sb.Append($"<section><h2>{H(d.Name)}</h2><div class=\"src\">{H(d.Category.ToString())} · {H(d.Source)}</div>");
            foreach (var (section, props) in Sections(d))
            {
                sb.Append($"<h3>{H(section)}</h3><div class=\"wrap\"><table>");
                foreach (var p in props)
                {
                    var cls = p.IsAvailable ? "" : " class=\"na\"";
                    var note = !p.IsAvailable && p.Note is not null ? $"<div class=\"src\">{H(p.Note)}</div>" : "";
                    var src = p.Source is not null ? $" <span class=\"src\">· {H(p.Source)}</span>" : "";
                    sb.Append($"<tr><th>{H(p.Name)}</th><td{cls}>{H(FormatValue(p))}{src}{note}</td></tr>");
                }

                sb.Append("</table></div>");
            }

            sb.Append("</section>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string H(string? s) => ReportExportService.H(s);

    /// <summary>Case-insensitive search across device names and property names / values / sources.</summary>
    public static IReadOnlyList<PropertyMatch> Search(HardwareInventory inventory, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        var list = new List<PropertyMatch>();
        foreach (var d in inventory.Devices)
        {
            var deviceHit = d.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
            foreach (var p in d.Properties)
            {
                if (deviceHit
                    || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || p.Value.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || (p.Source?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false))
                {
                    list.Add(new PropertyMatch(d, p));
                }
            }
        }

        return list;
    }
}
