using System.Text;
using System.Text.Json;
using HackWizard.Core.Models;
using HackWizard.Core.Rules;
using static HackWizard.Core.Reports.Html;

namespace HackWizard.Core.Reports;

public static class ReportWriter
{
    public static string WriteJson(HardwareReport r, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(r, KnowledgeBase.JsonOptions), new UTF8Encoding(false));
        return path;
    }

    public static string WriteHtml(HardwareReport r, CompatibilityResult? c, string path)
    {
        var b = new StringBuilder();
        var s = r.System;
        b.Append("<h2>System</h2><table>")
         .Append(Row("Manufacturer / model", $"{s.Manufacturer} {s.Version} ({s.Model})"))
         .Append(Row("Baseboard", s.Baseboard))
         .Append(Row("Machine type", s.MachineType))
         .Append(Row("BIOS", $"{s.BiosVendor} {s.BiosVersion} ({s.BiosDate})"))
         .Append(Row("CPU", $"{r.Cpu.Name} — {r.Cpu.Vendor} family 0x{r.Cpu.Family:X} model 0x{r.Cpu.Model:X} stepping {r.Cpu.Stepping} — {r.Cpu.Codename} — {r.Cpu.PhysicalCores} cores / {r.Cpu.LogicalProcessors} threads"))
         .Append(Row("Architecture", r.HostArchitecture))
         .Append(Row("Memory", $"{s.MemoryBytes / 1024 / 1024 / 1024} GB"))
         .Append(Row("Display", $"{s.DisplayWidth} × {s.DisplayHeight}"))
         .Append(Row("Form factor", s.IsLaptop ? "Laptop" : "Desktop"))
         .Append(Row("Trackpad interface", r.TrackpadInterface))
         .Append(Row("MAC for ROM", r.EthernetMac))
         .Append(Row("DSDT symbols", string.Join(", ", r.DsdtSymbols)))
         .Append("</table>");

        if (c != null)
        {
            b.Append("<h2>Compatibility</h2>");
            if (c.IsBlocked) b.Append("<div class=\"box danger\"><b>Blocked:</b><ul>").Append(string.Concat(c.BlockingReasons.Select(x => $"<li>{E(x)}</li>"))).Append("</ul></div>");
            if (c.Warnings.Count > 0) b.Append("<div class=\"box warn\"><ul>").Append(string.Concat(c.Warnings.Select(x => $"<li>{E(x)}</li>"))).Append("</ul></div>");
            b.Append("<table><tr><th>Status</th><th>Component</th><th>Device</th><th>Notes</th></tr>");
            foreach (var i in c.Items.Where(x => x.Device != "-"))
                b.Append($"<tr><td class=\"{i.Status}\">{i.Status}</td><td>{E(i.Component)}</td><td>{E(i.Device)}</td><td>{E(i.Notes)}</td></tr>");
            b.Append("</table><p><b>Mac model (SMBIOS):</b> ").Append(E(c.Smbios)).Append(" &nbsp; <b>Recommended macOS:</b> ").Append(E(c.RecommendedMacos)).Append("</p>");
            b.Append("<p><b>Kexts:</b> ").Append(E(string.Join(", ", c.EffectiveKexts))).Append("<br><b>SSDTs:</b> ").Append(E(string.Join(", ", c.Ssdts))).Append("</p>");
        }

        b.Append("<h2>Devices</h2><table><tr><th>Category</th><th>Name</th><th>ID</th><th>ACPI path</th><th>OpenCore path</th></tr>");
        foreach (var d in r.Devices.OrderBy(x => x.Category).ThenBy(x => x.Name))
            b.Append($"<tr><td>{d.Category}</td><td>{E(d.Name)}</td><td>{E(d.PciIdKey)}</td><td>{E(d.AcpiPath)}</td><td>{E(d.OcDevicePath)}</td></tr>");
        b.Append("</table><h2>ACPI objects</h2><table><tr><th>HID</th><th>Path</th></tr>");
        foreach (var a in r.AcpiDevices) b.Append($"<tr><td>{E(a.Hid)}</td><td>{E(a.AcpiPath)}</td></tr>");
        b.Append("</table><h2>Disks</h2><table><tr><th>#</th><th>Model</th><th>Bus</th><th>Size</th><th>System/Boot</th></tr>");
        foreach (var d in r.Disks)
            b.Append($"<tr><td>{d.Number}</td><td>{E(d.Model)}</td><td>{E(d.BusType)}</td><td>{d.SizeBytes / 1_000_000_000} GB</td><td>{(d.IsSystem || d.IsBoot ? "Yes" : "")}</td></tr>");
        b.Append("</table>");
        if (r.Warnings.Count > 0) b.Append("<div class=\"box warn\"><b>Detection warnings</b><ul>").Append(string.Concat(r.Warnings.Select(w => $"<li>{E(w)}</li>"))).Append("</ul></div>");
        b.Append("<p>Want to help support more PCs? Attach <code>hardware-report.json</code> to a “Hardware report” issue on the project's GitHub.</p>");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Page("HackWizard hardware report", b.ToString()), new UTF8Encoding(false));
        return path;
    }

    private static string Row(string k, string v) => $"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>";
}
