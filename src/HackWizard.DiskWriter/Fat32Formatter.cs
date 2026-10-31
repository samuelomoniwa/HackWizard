using System.Buffers.Binary;
using System.Text;
using Serilog;

namespace HackWizard.DiskWriter;

public sealed record Fat32Layout(long TotalSectors, int BytesPerSector, int SectorsPerCluster, int ReservedSectors, int NumFats, uint FatSizeSectors, uint ClusterCount)
{
    public long DataStartSector => ReservedSectors + (long)NumFats * FatSizeSectors;
}

/// <summary>Native FAT32 formatter (Microsoft FAT specification / fat32format algorithm),
/// needed because Windows' own tools refuse FAT32 on volumes larger than 32 GB.</summary>
public static class Fat32Formatter
{
    public const int MinClusters = 65525;

    public static Fat32Layout ComputeLayout(long totalBytes, int bytesPerSector)
    {
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)) throw new ArgumentException("Unsupported sector size.");
        var totalSectors = Math.Min(totalBytes / bytesPerSector, uint.MaxValue);
        var gib = totalBytes / (1024.0 * 1024 * 1024);
        var clusterBytes = gib <= 8 ? 4096 : gib <= 16 ? 8192 : gib <= 32 ? 16384 : 32768;
        var spc = Math.Max(1, clusterBytes / bytesPerSector);
        const int numFats = 2;
        var reserved = 32;
        var fatSize = (uint)(4L * (totalSectors - reserved) / ((long)spc * bytesPerSector + 4L * numFats) + 1);
        var align = Math.Max(1, 1024 * 1024 / bytesPerSector);
        var dataStart = reserved + (long)numFats * fatSize;
        reserved += (int)((align - dataStart % align) % align);
        if (reserved > ushort.MaxValue) throw new InvalidOperationException("Reserved area too large.");
        var clusters = (uint)((totalSectors - reserved - (long)numFats * fatSize) / spc);
        if (clusters < MinClusters) throw new InvalidOperationException($"Volume too small for FAT32 ({clusters} clusters).");
        if (clusters > 0x0FFFFFF5) throw new InvalidOperationException("Volume too large for FAT32.");
        if ((long)fatSize * bytesPerSector / 4 < clusters + 2) throw new InvalidOperationException("FAT size calculation error.");
        return new Fat32Layout(totalSectors, bytesPerSector, spc, reserved, numFats, fatSize, clusters);
    }

    public static Fat32Layout Format(IBlockDevice dev, string label, uint hiddenSectors, uint? volumeId = null, IProgress<double>? progress = null)
    {
        var l = ComputeLayout(dev.Length, dev.SectorSize);
        var bps = l.BytesPerSector;
        var volId = volumeId ?? (uint)(DateTime.UtcNow.Ticks ^ Environment.TickCount64);
        Log.Information("FAT32 format: {Sectors} sectors, {Spc} sec/cluster, reserved {Res}, FAT {Fat} sectors, {Clusters} clusters", l.TotalSectors, l.SectorsPerCluster, l.ReservedSectors, l.FatSizeSectors, l.ClusterCount);

        var zeroSectors = l.DataStartSector + l.SectorsPerCluster;
        var chunk = new byte[1024 * 1024 / bps * bps];
        long done = 0;
        while (done < zeroSectors)
        {
            var n = (int)Math.Min(chunk.Length / bps, zeroSectors - done);
            dev.Write(done * bps, chunk.AsSpan(0, n * bps));
            done += n;
            progress?.Report(done * 100.0 / zeroSectors);
        }

        var boot = BuildBootSector(l, label, hiddenSectors, volId);
        var fsInfo = BuildFsInfo(l);
        dev.Write(0, boot); dev.Write(1L * bps, fsInfo);
        dev.Write(6L * bps, boot); dev.Write(7L * bps, fsInfo);

        var fat0 = new byte[bps];
        BinaryPrimitives.WriteUInt32LittleEndian(fat0.AsSpan(0), 0x0FFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fat0.AsSpan(4), 0x0FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat0.AsSpan(8), 0x0FFFFFFF);
        for (var f = 0; f < l.NumFats; f++) dev.Write((l.ReservedSectors + (long)f * l.FatSizeSectors) * bps, fat0);

        var root = new byte[bps];
        Encoding.ASCII.GetBytes(NormalizeLabel(label)).CopyTo(root, 0);
        root[11] = 0x08;
        dev.Write(l.DataStartSector * bps, root);
        dev.Flush();
        return l;
    }

    public static string NormalizeLabel(string label)
    {
        var clean = new string(label.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-').ToArray());
        return (clean.Length == 0 ? "NO NAME" : clean).PadRight(11)[..11];
    }

    internal static byte[] BuildBootSector(Fat32Layout l, string label, uint hidden, uint volId)
    {
        var b = new byte[l.BytesPerSector];
        b[0] = 0xEB; b[1] = 0x58; b[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(b, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(11), (ushort)l.BytesPerSector);
        b[13] = (byte)l.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)l.ReservedSectors);
        b[16] = (byte)l.NumFats;
        b[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(24), 63);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), 255);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), hidden);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), (uint)l.TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), l.FatSizeSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(48), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(50), 6);
        b[64] = 0x80; b[66] = 0x29;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(67), volId);
        Encoding.ASCII.GetBytes(NormalizeLabel(label)).CopyTo(b, 71);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(b, 82);
        b[90] = 0xCD; b[91] = 0x18;
        b[510] = 0x55; b[511] = 0xAA;
        return b;
    }

    internal static byte[] BuildFsInfo(Fat32Layout l)
    {
        var s = new byte[l.BytesPerSector];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0), 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(484), 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(488), l.ClusterCount - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(492), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(508), 0xAA550000);
        return s;
    }
}
