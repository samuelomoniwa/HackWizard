using System.Runtime.InteropServices;
using HackWizard.Core.Detection;
using HackWizard.Core.Logging;
using HackWizard.Core.Native;
using Serilog;

namespace HackWizard.DiskWriter;

public sealed record UsbDisk(int Number, string Model, string Serial, long SizeBytes, string BusType)
{
    public string Display => $"Disk {Number}: {Model} — {SizeBytes / 1_000_000_000.0:F1} GB — S/N {(string.IsNullOrEmpty(Serial) ? "n/a" : Serial)}";
}

/// <summary>Lists ONLY USB-bus disks that are safe targets. Internal, system, boot and page-file disks are never listed.</summary>
public static class UsbDisks
{
    public const long MinSize = 8L * 1000 * 1000 * 1000 - 512L * 1024 * 1024;

    public static List<UsbDisk> List()
    {
        var protectedDisks = ProtectedDiskNumbers();
        var wmiUsb = WmiHelper.Cimv2("SELECT Index, InterfaceType FROM Win32_DiskDrive")
            .Where(d => WmiHelper.S(d, "InterfaceType") == "USB").Select(d => (int)WmiHelper.L(d, "Index")).ToHashSet();
        var result = new List<UsbDisk>();
        foreach (var d in WmiHelper.Query(@"root\Microsoft\Windows\Storage", "SELECT Number, FriendlyName, SerialNumber, Size, BusType, IsSystem, IsBoot, IsReadOnly FROM MSFT_Disk"))
        {
            var n = (int)WmiHelper.L(d, "Number");
            var reasons = new List<string>();
            if (WmiHelper.L(d, "BusType") != 7) reasons.Add("not USB bus");
            if (!wmiUsb.Contains(n)) reasons.Add("Win32_DiskDrive does not report USB");
            if (WmiHelper.B(d, "IsSystem") || WmiHelper.B(d, "IsBoot")) reasons.Add("system/boot disk");
            if (protectedDisks.Contains(n)) reasons.Add("hosts Windows, page file or HackWizard data");
            if (WmiHelper.L(d, "Size") < MinSize) reasons.Add("smaller than 8 GB");
            if (WmiHelper.B(d, "IsReadOnly")) reasons.Add("read-only");
            if (reasons.Count > 0) { Log.Debug("Disk {N} excluded: {Reasons}", n, string.Join(", ", reasons)); continue; }
            result.Add(new UsbDisk(n, WmiHelper.S(d, "FriendlyName"), WmiHelper.S(d, "SerialNumber"), WmiHelper.L(d, "Size"), "USB"));
        }
        return result;
    }

    public static UsbDisk ValidateTarget(int diskNumber, string expectedSerial, long expectedSize)
    {
        var d = List().FirstOrDefault(x => x.Number == diskNumber)
                ?? throw new InvalidOperationException($"Disk {diskNumber} is no longer a valid USB target. Nothing was written.");
        if (d.Serial != expectedSerial || d.SizeBytes != expectedSize)
            throw new InvalidOperationException($"Disk {diskNumber} changed (different serial/size) since you selected it. Nothing was written. Refresh and select again.");
        return d;
    }

    public static HashSet<int> ProtectedDiskNumbers()
    {
        var set = new HashSet<int>(); var letters = new HashSet<char>();
        void AddPath(string? p) { if (!string.IsNullOrEmpty(p) && p.Length >= 2 && p[1] == ':') letters.Add(char.ToUpperInvariant(p[0])); }
        AddPath(Environment.GetEnvironmentVariable("SystemDrive"));
        AddPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddPath(AppPaths.Root);
        AddPath(Environment.ProcessPath);
        foreach (var pf in WmiHelper.Cimv2("SELECT Name FROM Win32_PageFileUsage")) AddPath(WmiHelper.S(pf, "Name"));
        foreach (var l in letters) if (DiskNumberOf(l) is { } n) set.Add(n);
        return set;
    }

    public static int? DiskNumberOf(char letter)
    {
        using var h = NativeMethods.CreateFileW($@"\\.\{letter}:", 0, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var size = Marshal.SizeOf<NativeMethods.STORAGE_DEVICE_NUMBER>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStructure<NativeMethods.STORAGE_DEVICE_NUMBER>(buf).DeviceNumber;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static char? DriveLetterOfDisk(int diskNumber)
    {
        foreach (var p in WmiHelper.Query(@"root\Microsoft\Windows\Storage", $"SELECT DriveLetter, DiskNumber FROM MSFT_Partition WHERE DiskNumber = {diskNumber}"))
        {
            var o = p["DriveLetter"];
            var c = o is char ch ? ch : o is ushort u ? (char)u : '\0';
            if (char.IsLetter(c)) return char.ToUpperInvariant(c);
        }
        return null;
    }
}
