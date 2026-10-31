using System.Text;

namespace HackWizard.Core.Plist;

/// <summary>Flattens two plists into path=value lines and reports added/removed/changed entries.</summary>
public static class PlistDiff
{
    public static IReadOnlyList<string> Diff(PNode before, PNode after)
    {
        var a = Flatten(before); var b = Flatten(after);
        var result = new List<string>();
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var inA = a.TryGetValue(key, out var va); var inB = b.TryGetValue(key, out var vb);
            if (inA && !inB) result.Add($"- {key} = {va}");
            else if (!inA && inB) result.Add($"+ {key} = {vb}");
            else if (va != vb) result.Add($"~ {key}: {va}  ->  {vb}");
        }
        return result;
    }

    public static Dictionary<string, string> Flatten(PNode root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, "", map);
        return map;
    }

    private static void Walk(PNode n, string prefix, Dictionary<string, string> map)
    {
        switch (n)
        {
            case PDict d:
                if (d.Count == 0) map[prefix] = "{}";
                foreach (var kv in d) Walk(kv.Value, prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key, map);
                break;
            case PArray a:
                if (a.Count == 0) map[prefix] = "[]";
                for (var i = 0; i < a.Count; i++) Walk(a[i], $"{prefix}[{i}]", map);
                break;
            default:
                map[prefix] = $"{n.TypeName}:{n}";
                break;
        }
    }

    public static string ToText(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.AppendLine(l);
        return sb.ToString();
    }
}
