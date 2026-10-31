using HackWizard.Core.Plist;

namespace HackWizard.Core.Efi;

/// <summary>Metadata read from a kext's Contents/Info.plist (used to fill Kernel→Add entries).</summary>
public sealed class KextInfo
{
    /// <summary>Path relative to EFI/OC/Kexts, e.g. "VoodooPS2Controller.kext/Contents/PlugIns/VoodooPS2Keyboard.kext".</summary>
    public string BundlePath { get; init; } = "";
    public string BundleId { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>"Contents/MacOS/X" or "" for codeless (plist-only) kexts.</summary>
    public string ExecutablePath { get; init; } = "";
    public string PlistPath { get; init; } = "Contents/Info.plist";
    public List<string> Dependencies { get; init; } = new();
    public string? ParentBundlePath { get; init; }
    public bool Enabled { get; set; } = true;
    public string Comment { get; set; } = "";
    public string Name => BundlePath.Split('/').Last();

    public static List<KextInfo> ReadWithPlugins(string kextDir, string bundlePath, string? parent = null)
    {
        var list = new List<KextInfo>();
        var plistFile = Path.Combine(kextDir, "Contents", "Info.plist");
        if (!File.Exists(plistFile)) throw new InvalidDataException($"{bundlePath}: Contents/Info.plist missing — not a valid kext.");
        var info = PlistReader.ReadFile(plistFile) as PDict ?? throw new InvalidDataException($"{bundlePath}: Info.plist is not a dict.");
        list.Add(FromInfoPlist(info, bundlePath, parent, exe => File.Exists(Path.Combine(kextDir, "Contents", "MacOS", exe))));
        var plugins = Path.Combine(kextDir, "Contents", "PlugIns");
        if (Directory.Exists(plugins))
            foreach (var p in Directory.GetDirectories(plugins, "*.kext").OrderBy(p => p, StringComparer.Ordinal))
                list.AddRange(ReadWithPlugins(p, $"{bundlePath}/Contents/PlugIns/{Path.GetFileName(p)}", bundlePath));
        return list;
    }

    public static KextInfo FromInfoPlist(PDict info, string bundlePath, string? parent, Func<string, bool> executableExists)
    {
        var exe = info.Str("CFBundleExecutable");
        return new KextInfo
        {
            BundlePath = bundlePath,
            BundleId = info.Str("CFBundleIdentifier") ?? throw new InvalidDataException($"{bundlePath}: CFBundleIdentifier missing."),
            Version = info.Str("CFBundleVersion") ?? info.Str("CFBundleShortVersionString") ?? "0",
            ExecutablePath = !string.IsNullOrEmpty(exe) && executableExists(exe) ? $"Contents/MacOS/{exe}" : "",
            Dependencies = (info["OSBundleLibraries"] as PDict)?.Keys.ToList() ?? new List<string>(),
            ParentBundlePath = parent
        };
    }
}

/// <summary>Valid OpenCore kext load order: Lilu first, VirtualSMC next, each kext after the kexts it depends on
/// (OSBundleLibraries), plugins after their parent; duplicate bundle identifiers are disabled (highest version kept).</summary>
public static class KextOrderer
{
    private static readonly string[] Pinned = { "as.vit9696.Lilu", "as.vit9696.VirtualSMC" };

    public static List<KextInfo> Order(IEnumerable<KextInfo> input)
    {
        var all = input.ToList();
        foreach (var g in all.GroupBy(k => k.BundleId).Where(g => g.Count() > 1))
        {
            var keep = g.OrderByDescending(k => ParseVersion(k.Version)).ThenBy(k => all.IndexOf(k)).First();
            foreach (var k in g.Where(x => !ReferenceEquals(x, keep))) { k.Enabled = false; k.Comment = $"Disabled: duplicate of {keep.BundlePath}"; }
        }
        var baseOrder = all.OrderBy(k => Array.IndexOf(Pinned, k.BundleId) is var i && i >= 0 ? i : Pinned.Length).ThenBy(k => all.IndexOf(k)).ToList();
        var enabledIds = all.Where(k => k.Enabled).Select(k => k.BundleId).ToHashSet();
        var placed = new List<KextInfo>(); var placedIds = new HashSet<string>();
        var remaining = new List<KextInfo>(baseOrder);
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(k =>
                k.Dependencies.Where(enabledIds.Contains).All(placedIds.Contains) &&
                (k.ParentBundlePath is null || placed.Any(p => p.BundlePath == k.ParentBundlePath)));
            if (next is null) throw new InvalidOperationException("Kext dependency cycle or missing parent: " + string.Join(", ", remaining.Select(r => r.BundlePath)));
            placed.Add(next); remaining.Remove(next);
            if (next.Enabled) placedIds.Add(next.BundleId);
        }
        return placed;
    }

    private static Version ParseVersion(string v)
    {
        var clean = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(clean.Contains('.') ? clean : clean + ".0", out var r) ? r : new Version(0, 0);
    }
}
