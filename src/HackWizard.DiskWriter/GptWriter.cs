using System.Buffers.Binary;
using System.Text;

namespace HackWizard.DiskWriter;

/// <summary>Writes a protective MBR + primary/backup GPT with a single Basic Data partition (used for image simulation).</summary>
public static class GptWriter
{
    public static readonly Guid BasicDataType = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public const int EntryCount = 128, EntrySize = 128;

    public static (long firstLba, long lastLba) Write(IBlockDevice disk, string partitionName, Guid? diskGuid = null, Guid? partGuid = null)
    {
        var ss = disk.SectorSize;
        var totalLba = disk.Length / ss;
        var entrySectors = EntryCount * EntrySize / ss;
        var lastUsable = totalLba - 1 - entrySectors - 1;
        var firstLba = 1024L * 1024 / ss;

        var mbr = new byte[ss];
        mbr[446 + 2] = 0x02; mbr[446 + 4] = 0xEE;
        mbr[446 + 5] = 0xFF; mbr[446 + 6] = 0xFF; mbr[446 + 7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 12), (uint)Math.Min(totalLba - 1, uint.MaxValue));
        mbr[510] = 0x55; mbr[511] = 0xAA;
        disk.Write(0, mbr);

        var entries = new byte[EntryCount * EntrySize];
        BasicDataType.ToByteArray().CopyTo(entries, 0);
        (partGuid ?? Guid.NewGuid()).ToByteArray().CopyTo(entries, 16);
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(32), firstLba);
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(40), lastUsable);
        Encoding.Unicode.GetBytes(partitionName.Length > 36 ? partitionName[..36] : partitionName).CopyTo(entries, 56);
        var entriesCrc = Crc32.Compute(entries);
        var dg = diskGuid ?? Guid.NewGuid();

        byte[] Header(long myLba, long altLba, long entriesLba)
        {
            var h = new byte[ss];
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(h, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), myLba);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), altLba);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 2 + entrySectors);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), lastUsable);
            dg.ToByteArray().CopyTo(h, 56);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), EntryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), entriesCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32.Compute(h.AsSpan(0, 92)));
            return h;
        }

        disk.Write(2L * ss, entries);
        disk.Write(1L * ss, Header(1, totalLba - 1, 2));
        disk.Write((totalLba - 1 - entrySectors) * ss, entries);
        disk.Write((totalLba - 1) * ss, Header(totalLba - 1, 1, totalLba - 1 - entrySectors));
        disk.Flush();
        return (firstLba, lastUsable);
    }
}

public static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
