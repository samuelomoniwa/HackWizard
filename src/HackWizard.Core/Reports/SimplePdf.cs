using System.Text;

namespace HackWizard.Core.Reports;

/// <summary>Dependency-free PDF writer for plain-text documents (A4, Helvetica, word-wrapped).</summary>
public static class SimplePdf
{
    private const int PageW = 595, PageH = 842, Margin = 50, Leading = 14, FontSize = 10, MaxChars = 95;

    public static byte[] Create(string title, IEnumerable<(bool Heading, string Text)> paragraphs)
    {
        var lines = new List<(bool, string)>();
        foreach (var l in Wrap(Sanitize(title), 80)) lines.Add((true, l));
        lines.Add((false, ""));
        foreach (var (h, t) in paragraphs)
        {
            foreach (var l in Wrap(Sanitize(t), h ? 80 : MaxChars)) lines.Add((h, l));
            lines.Add((false, ""));
        }
        var pages = lines.Chunk((PageH - 2 * Margin) / Leading).ToList();
        var objs = new List<string>();
        var pageIds = Enumerable.Range(0, pages.Count).Select(i => 5 + i * 2).ToList();
        objs.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objs.Add($"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pages.Count} >>");
        objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var p = 0; p < pages.Count; p++)
        {
            var sb = new StringBuilder($"BT {Margin} {PageH - Margin} Td {Leading} TL\n");
            foreach (var (h, l) in pages[p]) sb.Append(h ? "/F2 11 Tf " : $"/F1 {FontSize} Tf ").Append('(').Append(Escape(l)).Append(") '\n");
            sb.Append($"/F1 8 Tf 0 -{Leading} Td (Page {p + 1} of {pages.Count}) Tj\nET");
            var content = sb.ToString();
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageW} {PageH}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageIds[p] + 1} 0 R >>");
            objs.Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objs.Count; i++) { offsets.Add(ms.Position); W($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = ms.Position;
        W($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Sanitize(string s) => new(s.Replace("→", "->").Replace("—", "-").Replace("–", "-").Replace("’", "'").Replace("“", "\"").Replace("”", "\"").Replace("…", "...").Replace("×", "x")
        .Select(c => c < 256 ? c : '?').ToArray());

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var para in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in para.Split(' '))
            {
                if (line.Length + word.Length + 1 > width && line.Length > 0) { yield return line.ToString(); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            yield return line.ToString();
        }
    }

    public static byte[] FromBiosGuide(BiosGuide g)
    {
        var paras = new List<(bool, string)>
        {
            (false, $"Enter setup: power on and tap {g.SetupKey}. Boot menu: {g.BootMenuKey}."),
            (false, g.SetupNotes),
            (false, "If a supervisor/administrator password is set you need it to change settings.")
        };
        var n = 1;
        foreach (var s in g.Settings)
        {
            paras.Add((true, $"{n++}. {s.Title} {(s.Required ? "(required)" : "(recommended)")}"));
            paras.Add((false, $"Where: {s.MenuPath}\nSet to: {s.Value}\nWhy: {s.Why}" + (string.IsNullOrEmpty(s.IfMissing) ? "" : $"\nIf you don't see it: {s.IfMissing}")));
        }
        return Create(g.Title, paras);
    }
}
