using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HackWizard.Core.Native;

public static class NativeMethods
{
    public enum FirmwareType : uint { Unknown = 0, Bios = 1, Uefi = 2, Max = 3 }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFirmwareType(out FirmwareType type);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFirmwareEnvironmentVariableW(string lpName, string lpGuid, IntPtr pBuffer, uint nSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint EnumSystemFirmwareTables(uint provider, byte[]? buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    public const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_SET_SPARSE = 0x000900C4;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    public const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;

    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY { public long Cylinders; public int MediaType; public int TracksPerCylinder; public int SectorsPerTrack; public int BytesPerSector; }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_NUMBER { public int DeviceType; public int DeviceNumber; public int PartitionNumber; }

    /// <summary>Converts a 4-char signature ("ACPI") into the DWORD expected by the firmware-table API.</summary>
    public static uint Sig(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    /// <summary>True when Windows itself runs on ARM64 (x64 apps are emulated there). Such PCs cannot run x86 macOS.</summary>
    public static bool IsArm64Host()
    {
        try
        {
            if (IsWow64Process2(GetCurrentProcess(), out _, out var native)) return native == IMAGE_FILE_MACHINE_ARM64;
        }
        catch (EntryPointNotFoundException) { }
        return RuntimeInformation.OSArchitecture == Architecture.Arm64;
    }
}
