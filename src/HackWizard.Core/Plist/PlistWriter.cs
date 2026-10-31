using System.Text;

namespace HackWizard.Core.Plist;

/// <summary>Deterministic XML plist serializer: tabs, LF line endings, UTF-8 without BOM (ProperTree/Sample.plist style).</summary>
public static class PlistWriter
{
    public static string ToXml(PNode root)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        Write(sb, root, 0);
        sb.Append("</plist>\n");
        return sb.ToString();
    }

    public static void WriteFile(PNode root, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToXml(root), new UTF8Encoding(false));
    }

    private static void Indent(StringBuilder sb, int level) => sb.Append('\t', level);

    private static void Write(StringBuilder sb, PNode node, int level)
    {
        Indent(sb, level);
        switch (node)
        {
            case PDict d:
                if (d.Count == 0) { sb.Append("<dict/>\n"); return; }
                sb.Append("<dict>\n");
                foreach (var kv in d)
                {
                    Indent(sb, level + 1);
                    sb.Append("<key>").Append(Escape(kv.Key)).Append("</key>\n");
                    Write(sb, kv.Value, level + 1);
                }
                Indent(sb, level); sb.Append("</dict>\n");
                return;
            case PArray a:
                if (a.Count == 0) { sb.Append("<array/>\n"); return; }
                sb.Append("<array>\n");
                foreach (var i in a) Write(sb, i, level + 1);
                Indent(sb, level); sb.Append("</array>\n");
                return;
            case PString s: sb.Append("<string>").Append(Escape(s.Value)).Append("</string>\n"); return;
            case PInteger n: sb.Append("<integer>").Append(n.ToString()).Append("</integer>\n"); return;
            case PReal r: sb.Append("<real>").Append(r.ToString()).Append("</real>\n"); return;
            case PBool b: sb.Append(b.Value ? "<true/>\n" : "<false/>\n"); return;
            case PData data: sb.Append("<data>").Append(Convert.ToBase64String(data.Value)).Append("</data>\n"); return;
            case PDate dt: sb.Append("<date>").Append(dt.ToString()).Append("</date>\n"); return;
            default: throw new PlistException($"Unknown node type {node.GetType().Name}");
        }
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
