using System.Text.RegularExpressions;

namespace HackWizard.Core.Detection;

public static partial class IdParser
{
    [GeneratedRegex(@"(?:VEN|VID)_(?<v>[0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)] private static partial Regex Ven();
    [GeneratedRegex(@"(?:DEV|PID)_(?<d>[0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)] private static partial Regex Dev();
    [GeneratedRegex(@"SUBSYS_(?<s>[0-9A-Fa-f]{8})", RegexOptions.IgnoreCase)] private static partial Regex Sub();

    public static (string Vendor, string Device, string Subsys) Parse(string pnpId)
    {
        var v = Ven().Match(pnpId); var d = Dev().Match(pnpId); var s = Sub().Match(pnpId);
        return (v.Success ? v.Groups["v"].Value.ToUpperInvariant() : "",
                d.Success ? d.Groups["d"].Value.ToUpperInvariant() : "",
                s.Success ? s.Groups["s"].Value.ToUpperInvariant() : "");
    }

    public static string BusOf(string pnpId)
    {
        var i = pnpId.IndexOf('\\');
        return i > 0 ? pnpId[..i].ToUpperInvariant() : pnpId.ToUpperInvariant();
    }

    public static string BusTypeName(long msftBusType) => msftBusType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 6 => "FibreChannel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "FileBackedVirtual", 16 => "StorageSpaces", 17 => "NVMe",
        _ => "Unknown"
    };
}
