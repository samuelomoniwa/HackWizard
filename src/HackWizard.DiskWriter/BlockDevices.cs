using System.Runtime.InteropServices;
using HackWizard.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace HackWizard.DiskWriter;

/// <summary>Sector-addressed write target (a real volume, an image file region, or memory for tests).</summary>
public interface IBlockDevice : IDisposable
{
    long Length { get; }
    int SectorSize { get; }
    void Write(long offset, ReadOnlySpan<byte> data);
    void Read(long offset, Span<byte> data);
    void Flush();
}

/// <summary>Raw access to a mounted volume (\\.\E:), locked and dismounted for exclusive access.</summary>
public sealed class VolumeDevice : IBlockDevice
{
    private readonly SafeFileHandle _h;
    private readonly FileStream _fs;
    public long Length { get; }
    public int SectorSize { get; }
    public long PartitionOffset { get; }

    public VolumeDevice(char driveLetter)
    {
        _h = NativeMethods.CreateFileW($@"\\.\{driveLetter}:", NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
        if (_h.IsInvalid) throw new IOException($"Cannot open volume {driveLetter}: (Win32 error {Marshal.GetLastWin32Error()}). Close any Explorer windows showing the drive and retry.");
        var locked = false;
        for (var i = 0; i < 10 && !locked; i++) { locked = Ioctl(NativeMethods.FSCTL_LOCK_VOLUME); if (!locked) Thread.Sleep(500); }
        if (!locked) { _h.Dispose(); throw new IOException($"Volume {driveLetter}: is in use and could not be locked. Close programs using the USB drive and retry."); }
        Ioctl(NativeMethods.FSCTL_DISMOUNT_VOLUME);
        Length = QueryLong(NativeMethods.IOCTL_DISK_GET_LENGTH_INFO, 8, 0);
        SectorSize = QuerySectorSize();
        PartitionOffset = QueryLong(NativeMethods.IOCTL_DISK_GET_PARTITION_INFO_EX, 144, 8);
        _fs = new FileStream(_h, FileAccess.ReadWrite, 0);
    }

    private bool Ioctl(uint code) => NativeMethods.DeviceIoControl(_h, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    private long QueryLong(uint code, int outSize, int fieldOffset)
    {
        var buf = Marshal.AllocHGlobal(outSize);
        try
        {
            if (!NativeMethods.DeviceIoControl(_h, code, IntPtr.Zero, 0, buf, outSize, out _, IntPtr.Zero))
                throw new IOException($"DeviceIoControl 0x{code:X} failed (Win32 error {Marshal.GetLastWin32Error()}).");
            return Marshal.ReadInt64(buf, fieldOffset);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private int QuerySectorSize()
    {
        var size = Marshal.SizeOf<NativeMethods.DISK_GEOMETRY>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.DeviceIoControl(_h, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY, IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero)) return 512;
            var g = Marshal.PtrToStructure<NativeMethods.DISK_GEOMETRY>(buf);
            return g.BytesPerSector > 0 ? g.BytesPerSector : 512;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset % SectorSize != 0 || data.Length % SectorSize != 0) throw new ArgumentException("Unaligned raw volume write.");
        _fs.Position = offset; _fs.Write(data);
    }

    public void Read(long offset, Span<byte> data) { _fs.Position = offset; _fs.ReadExactly(data); }
    public void Flush() => _fs.Flush(true);

    public void Dispose()
    {
        try { _fs.Flush(true); } catch { }
        Ioctl(NativeMethods.FSCTL_UNLOCK_VOLUME);
        _fs.Dispose();
        _h.Dispose();
    }
}

public sealed class FileRegionDevice : IBlockDevice
{
    private readonly FileStream _fs; private readonly long _offset; private readonly bool _own;
    public long Length { get; }
    public int SectorSize { get; }
    public FileRegionDevice(FileStream fs, long offset, long length, int sectorSize = 512, bool ownsStream = false)
    { _fs = fs; _offset = offset; Length = length; SectorSize = sectorSize; _own = ownsStream; }
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        _fs.Position = _offset + offset; _fs.Write(data);
    }
    public void Read(long offset, Span<byte> data) { _fs.Position = _offset + offset; _fs.ReadExactly(data); }
    public void Flush() => _fs.Flush();
    public void Dispose() { if (_own) _fs.Dispose(); }
}

public sealed class MemoryDevice : IBlockDevice
{
    public byte[] Data { get; }
    public long Length => Data.Length;
    public int SectorSize { get; }
    public MemoryDevice(long size, int sectorSize = 512) { Data = new byte[size]; SectorSize = sectorSize; }
    public void Write(long offset, ReadOnlySpan<byte> data) => data.CopyTo(Data.AsSpan((int)offset));
    public void Read(long offset, Span<byte> data) => Data.AsSpan((int)offset, data.Length).CopyTo(data);
    public void Flush() { }
    public void Dispose() { }
}
