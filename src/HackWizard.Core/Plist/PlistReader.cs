using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace HackWizard.Core.Plist;

/// <summary>XML property-list parser (Apple DTD 1.0). Preserves dictionary key order.</summary>
public static class PlistReader
{
    public static PNode ReadFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static PNode Read(Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        using var xr = XmlReader.Create(stream, settings);
        var doc = XDocument.Load(xr, LoadOptions.None);
        var plist = doc.Root ?? throw new PlistException("Empty plist.");
        if (plist.Name.LocalName != "plist") throw new PlistException("Root element is not <plist>.");
        var first = plist.Elements().FirstOrDefault() ?? throw new PlistException("<plist> has no content.");
        return ParseElement(first);
    }

    public static PNode ReadString(string xml)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return Read(ms);
    }

    private static PNode ParseElement(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "dict":
            {
                var d = new PDict();
                var children = e.Elements().ToList();
                for (var i = 0; i < children.Count; i += 2)
                {
                    if (children[i].Name.LocalName != "key") throw new PlistException($"Expected <key>, got <{children[i].Name.LocalName}>.");
                    if (i + 1 >= children.Count) throw new PlistException($"Key '{children[i].Value}' has no value.");
                    d[children[i].Value] = ParseElement(children[i + 1]);
                }
                return d;
            }
            case "array": return new PArray(e.Elements().Select(ParseElement));
            case "string": return new PString(e.Value);
            case "integer":
            {
                var s = e.Value.Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return new PInteger(long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                return new PInteger(long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture));
            }
            case "real": return new PReal(double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture));
            case "true": return new PBool(true);
            case "false": return new PBool(false);
            case "data":
            {
                var b64 = new string(e.Value.Where(c => !char.IsWhiteSpace(c)).ToArray());
                return new PData(Convert.FromBase64String(b64));
            }
            case "date": return new PDate(DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
            default: throw new PlistException($"Unsupported plist element <{e.Name.LocalName}>.");
        }
    }
}
