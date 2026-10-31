using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HackWizard.Core.Native;
using Serilog;

namespace HackWizard.DiskWriter;

public enum WriteMode { DryRun, SimulateImage, RealDisk }

public sealed record WriteProgress(string Stage, double Percent, string Detail);

public sealed class WriteOptions
{
    public WriteMode Mode { get; init; } = WriteMode.DryRun;
    public UsbDisk? Target { get; init; }
    public string? ImagePath { get; init; }
    public string Label { get; init; } = "OPENCORE";
    public long PartitionCapBytes { get; init; } = 16L * 1024 * 1024 * 1024;
}

/// <summary>Creates the bootable USB: GPT + one FAT32 partition (≤16 GB), copies the staged tree and verifies every file by SHA-256.</summary>
public sealed class UsbWriter
{
    public const long Fat32MaxFile = 4L * 1024 * 1024 * 1024 - 1;

    public async Task<string> WriteAsync(string stagingDir, WriteOptions opt, IProgress<WriteProgress>? progress, CancellationToken ct)
    {
        var files = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
        var big = files.FirstOrDefault(f => new FileInfo(f).Length > Fat32MaxFile);
        if (big != null) throw new InvalidOperationException($"{big} is larger than FAT32's 4 GB file limit.");
        var total = files.Sum(f => new FileInfo(f).Length);
        if (total > opt.PartitionCapBytes - 64 * 1024 * 1024) throw new InvalidOperationException("Staged files do not fit on a 16 GB partition.");

        switch (opt.Mode)
        {
            case WriteMode.DryRun:
            {
                var sb = new StringBuilder("DRY RUN — nothing was written.\n");
                if (opt.Target is { } t)
                {
                    sb.AppendLine($"Would erase: {t.Display}");
                    sb.AppendLine("diskpart script:").AppendLine(DiskpartScript(t.Number, PartitionSizeMb(t.SizeBytes, opt.PartitionCapBytes)));
                }
                sb.AppendLine($"Would copy {files.Length} files ({total / 1024 / 1024} MB):");
                foreach (var f in files) sb.AppendLine("  " + Path.GetRelativePath(stagingDir, f));
                Log.Information("{DryRun}", sb.ToString());
                return sb.ToString();
            }
            case WriteMode.SimulateImage: return await Task.Run(() => Simulate(stagingDir, files, opt, progress, ct), ct).ConfigureAwait(false);
            case WriteMode.RealDisk: return await Task.Run(() => Real(stagingDir, files, opt, progress, ct), ct).ConfigureAwait(false);
            default: throw new ArgumentOutOfRangeException(nameof(opt));
        }
    }

    public static int? PartitionSizeMb(long diskBytes, long cap) => diskBytes > cap + 64L * 1024 * 1024 ? (int)(cap / 1024 / 1024) : null;

    public static string DiskpartScript(int disk, int? sizeMb) => string.Join("\r\n",
        $"select disk {disk}", "attributes disk clear readonly", "clean", "convert gpt",
        sizeMb is { } s ? $"create partition primary size={s}" : "create partition primary", "assign", "exit");

    private static string Simulate(string stagingDir, string[] files, WriteOptions opt, IProgress<WriteProgress>? progress, CancellationToken ct)
    {
        var img = opt.ImagePath ?? throw new ArgumentException("ImagePath required for simulation.");
        var total = files.Sum(f => new FileInfo(f).Length);
        var size = Math.Max(1L << 30, ((total + (512L << 20)) / (1 << 20) + 1) * (1 << 20));
        progress?.Report(new("Creating image", 5, $"{img} ({size >> 20} MB, sparse)"));
        using (var fs = new FileStream(img, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            NativeMethods.DeviceIoControl(fs.SafeFileHandle, NativeMethods.FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            fs.SetLength(size);
            using var whole = new FileRegionDevice(fs, 0, size);
            var (first, last) = GptWriter.Write(whole, opt.Label);
            progress?.Report(new("Formatting FAT32 (native)", 20, $"partition LBA {first}-{last}"));
            using var part = new FileRegionDevice(fs, first * 512, (last - first + 1) * 512);
            Fat32Formatter.Format(part, opt.Label, (uint)first, null, new Progress<double>(p => progress?.Report(new("Formatting FAT32 (native)", 20 + p * 0.3, ""))));
        }
        var mirror = img + ".contents";
        if (Directory.Exists(mirror)) Directory.Delete(mirror, true);
        CopyAndVerify(stagingDir, files, mirror, null, progress, ct);
        return $"Simulation complete.\nImage (GPT + FAT32): {img}\nFiles (verified copy): {mirror}\nNo physical disk was touched.";
    }

    private static string Real(string stagingDir, string[] files, WriteOptions opt, IProgress<WriteProgress>? progress, CancellationToken ct)
    {
        var t = opt.Target ?? throw new ArgumentException("Target required.");
        UsbDisks.ValidateTarget(t.Number, t.Serial, t.SizeBytes);
        progress?.Report(new("Partitioning", 2, $"Disk {t.Number}: clean, GPT, single partition"));
        RunDiskpart(DiskpartScript(t.Number, PartitionSizeMb(t.SizeBytes, opt.PartitionCapBytes)));
        UsbDisks.ValidateTarget(t.Number, t.Serial, t.SizeBytes);

        var letter = '\0';
        for (var i = 0; i < 20 && letter == '\0'; i++) { letter = UsbDisks.DriveLetterOfDisk(t.Number) ?? '\0'; if (letter == '\0') Thread.Sleep(500); }
        if (letter == '\0') throw new IOException("The new partition did not get a drive letter. Open Disk Management, assign a letter to the USB partition, and retry.");
        if (UsbDisks.DiskNumberOf(letter) != t.Number) throw new InvalidOperationException($"Safety stop: drive {letter}: is not on disk {t.Number}.");

        progress?.Report(new("Formatting FAT32 (native)", 10, $"{letter}:"));
        try
        {
            using var vol = new VolumeDevice(letter);
            Fat32Formatter.Format(vol, opt.Label, (uint)(vol.PartitionOffset / vol.SectorSize), null, new Progress<double>(p => progress?.Report(new("Formatting FAT32 (native)", 10 + p * 0.2, ""))));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Native FAT32 format failed; falling back to diskpart format (partition ≤ 32 GB)");
            progress?.Report(new("Formatting FAT32 (diskpart fallback)", 25, ex.Message));
            RunDiskpart($"select volume {letter}\r\nformat fs=fat32 quick label={opt.Label}\r\nexit");
        }

        var root = $"{letter}:\\";
        for (var i = 0; i < 20; i++) { try { if (new DriveInfo(root).DriveFormat == "FAT32") break; } catch { } Thread.Sleep(500); }
        if (new DriveInfo(root).DriveFormat != "FAT32") throw new IOException($"{root} did not mount as FAT32.");
        CopyAndVerify(stagingDir, files, root, letter, progress, ct);
        return $"USB drive {root} written and verified ({files.Length} files).";
    }

    private static void CopyAndVerify(string stagingDir, string[] files, string destRoot, char? letterToRemount, IProgress<WriteProgress>? progress, CancellationToken ct)
    {
        var total = files.Sum(f => new FileInfo(f).Length); long done = 0;
        var hashes = new Dictionary<string, string>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(stagingDir, f);
            var dst = Path.Combine(destRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            using (var src = File.OpenRead(f))
            using (var d = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buf = new byte[1 << 20]; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) { d.Write(buf, 0, n); sha.AppendData(buf, 0, n); done += n; progress?.Report(new("Copying", 30 + done * 50.0 / Math.Max(1, total), rel)); }
                hashes[rel] = Convert.ToHexString(sha.GetHashAndReset());
            }
        }
        if (letterToRemount is { } l)
        {
            using var h = NativeMethods.CreateFileW($@"\\.\{l}:", NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid && NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_UNLOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
        }
        var i = 0;
        foreach (var (rel, expected) in hashes)
        {
            ct.ThrowIfCancellationRequested();
            using var s = File.OpenRead(Path.Combine(destRoot, rel));
            if (Convert.ToHexString(SHA256.HashData(s)) != expected) throw new IOException($"Verification failed for {rel}: the USB drive may be faulty. Try another drive.");
            progress?.Report(new("Verifying", 80 + ++i * 20.0 / hashes.Count, rel));
        }
        Log.Information("Copied and verified {Count} files to {Dest}", hashes.Count, destRoot);
    }

    private static void RunDiskpart(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hackwizard-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, script, Encoding.ASCII);
        try
        {
            Log.Information("diskpart script:\n{Script}", script);
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{path}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(300_000)) { p.Kill(true); throw new TimeoutException("diskpart timed out."); }
            Log.Information("diskpart output (exit {Code}):\n{Out}", p.ExitCode, o);
            if (p.ExitCode != 0 || o.Contains("DiskPart has encountered an error", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"diskpart failed (exit {p.ExitCode}). Output:\n{o}\nTry another USB port/drive, or remove write protection.");
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
