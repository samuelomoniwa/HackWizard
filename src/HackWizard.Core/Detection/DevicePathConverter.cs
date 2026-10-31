using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HackWizard.Core.Detection;

/// <summary>Converts Windows DEVPKEY_Device_LocationPaths ("PCIROOT(0)#PCI(0801)#PCI(0006)")
/// into an OpenCore DeviceProperties path ("PciRoot(0x0)/Pci(0x8,0x1)/Pci(0x0,0x6)").</summary>
public static partial class DevicePathConverter
{
    [GeneratedRegex(@"^PCIROOT\((?<root>[0-9A-Fa-f]+)\)(?<rest>(#PCI\([0-9A-Fa-f]{4}\))+)$")]
    private static partial Regex PathRx();

    [GeneratedRegex(@"PCI\((?<dev>[0-9A-Fa-f]{2})(?<fn>[0-9A-Fa-f]{2})\)")]
    private static partial Regex NodeRx();

    public static string? ToOpenCore(IEnumerable<string>? locationPaths)
    {
        if (locationPaths is null) return null;
        foreach (var lp in locationPaths)
        {
            var m = PathRx().Match(lp.Trim());
            if (!m.Success) continue;
            var sb = new StringBuilder();
            sb.Append("PciRoot(0x").Append(int.Parse(m.Groups["root"].Value, NumberStyles.HexNumber).ToString("x")).Append(')');
            foreach (Match n in NodeRx().Matches(m.Groups["rest"].Value))
            {
                var dev = int.Parse(n.Groups["dev"].Value, NumberStyles.HexNumber);
                var fn = int.Parse(n.Groups["fn"].Value, NumberStyles.HexNumber);
                sb.Append("/Pci(0x").Append(dev.ToString("x")).Append(",0x").Append(fn.ToString("x")).Append(')');
            }
            return sb.ToString();
        }
        return null;
    }
}
