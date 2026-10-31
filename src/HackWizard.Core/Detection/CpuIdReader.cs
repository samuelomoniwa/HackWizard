using System.Runtime.Intrinsics.X86;
using System.Text;

namespace HackWizard.Core.Detection;

public readonly record struct CpuSignature(string Vendor, int Family, int Model, int Stepping);

public static class CpuIdReader
{
    public static CpuSignature Read()
    {
        if (!X86Base.IsSupported) return new("Unknown", 0, 0, 0);
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        var vendor = new StringBuilder();
        foreach (var r in new[] { ebx, edx, ecx }) vendor.Append(Encoding.ASCII.GetString(BitConverter.GetBytes(r)));
        var (eax1, _, _, _) = X86Base.CpuId(1, 0);
        return Decode(vendor.ToString(), eax1);
    }

    /// <summary>Decodes CPUID leaf 1 EAX into display family/model/stepping (Intel SDM / AMD APM rules).</summary>
    public static CpuSignature Decode(string vendor, int eax)
    {
        var stepping = eax & 0xF;
        var baseModel = (eax >> 4) & 0xF;
        var baseFamily = (eax >> 8) & 0xF;
        var extModel = (eax >> 16) & 0xF;
        var extFamily = (eax >> 20) & 0xFF;
        var family = baseFamily == 0xF ? baseFamily + extFamily : baseFamily;
        var model = (baseFamily == 0xF || baseFamily == 0x6) ? (extModel << 4) + baseModel : baseModel;
        return new(vendor, family, model, stepping);
    }
}
