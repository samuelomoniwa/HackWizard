using HackWizard.Core.Plist;

namespace HackWizard.Core.Efi;

/// <summary>Reads AMD_Vanilla patches.plist and sets the core count in the "cpuid_cores_per_package" patches
/// (second byte of each Replace value), as the Dortania AMD guide instructs.</summary>
public static class AmdVanillaPatcher
{
    public static PArray LoadAndPatch(PNode patchesPlist, int physicalCores)
    {
        if (physicalCores is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(physicalCores), "Core count must be 1-255.");
        var root = patchesPlist as PDict ?? throw new PlistException("patches.plist root is not a dict.");
        var result = (PArray)root.Dict("Kernel").Array("Patch").Clone();
        var patched = 0;
        foreach (var p in result.OfType<PDict>())
        {
            if (!(p.Str("Comment") ?? "").Contains("cpuid_cores_per_package", StringComparison.OrdinalIgnoreCase)) continue;
            var replace = p.Get<PData>("Replace").Value.ToArray();
            if (replace.Length < 2) throw new PlistException("Unexpected Replace length in a core-count patch.");
            replace[1] = (byte)physicalCores;
            p.Set("Replace", new PData(replace));
            patched++;
        }
        if (patched == 0) throw new PlistException("No 'cpuid_cores_per_package' patches found in AMD_Vanilla patches.plist — upstream format changed; update the knowledge base.");
        return result;
    }

    public static int CountCorePatches(PArray patches) =>
        patches.OfType<PDict>().Count(p => (p.Str("Comment") ?? "").Contains("cpuid_cores_per_package", StringComparison.OrdinalIgnoreCase));
}
