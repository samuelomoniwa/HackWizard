using HackWizard.Core.Plist;
using Serilog;

namespace HackWizard.Core.Efi;

/// <summary>Resolves valid layout-ids for any HDA codec from AppleALC's own source database
/// (Resources/&lt;Codec&gt;/Info.plist: CodecID + Files→Layouts→Id), so no codec table has to be maintained here.</summary>
public static class AppleAlcLayouts
{
    public sealed record Result(string CodecName, List<int> Layouts);

    /// <param name="root">Extracted AppleALC source tree (any directory containing Resources/).</param>
    /// <param name="codecKey">"VVVV:DDDD", e.g. "10EC:0256".</param>
    public static Result? Find(string root, string codecKey)
    {
        var parts = codecKey.Split(':');
        if (parts.Length != 2) return null;
        var codecId = (Convert.ToInt64(parts[0], 16) << 16) | Convert.ToInt64(parts[1], 16);
        var resources = Directory.GetDirectories(root, "Resources", SearchOption.AllDirectories)
            .FirstOrDefault(d => Directory.GetDirectories(d).Any(c => File.Exists(Path.Combine(c, "Info.plist"))));
        if (resources is null) return null;
        string? name = null; var layouts = new SortedSet<int>();
        foreach (var dir in Directory.GetDirectories(resources))
        {
            var file = Path.Combine(dir, "Info.plist");
            if (!File.Exists(file)) continue;
            try
            {
                if (PlistReader.ReadFile(file) is not PDict d || d["CodecID"] is not PInteger id || id.Value != codecId) continue;
                name ??= d.Str("CodecName") ?? Path.GetFileName(dir);
                if ((d["Files"] as PDict)?["Layouts"] is PArray arr)
                    foreach (var l in arr.OfType<PDict>()) if (l["Id"] is PInteger lid) layouts.Add((int)lid.Value);
            }
            catch (Exception ex) { Log.Debug(ex, "Skipping AppleALC resource {Dir}", dir); }
        }
        return name is null || layouts.Count == 0 ? null : new Result(name, layouts.ToList());
    }
}
