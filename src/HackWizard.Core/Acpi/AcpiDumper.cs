using System.Diagnostics;
using System.Text;
using HackWizard.Core.Native;
using Microsoft.Win32;
using Serilog;

namespace HackWizard.Core.Acpi;

public sealed record AcpiDumpResult(string Directory, IReadOnlyList<string> Files, IReadOnlyList<string> Notes);

/// <summary>Dumps ACPI tables on Windows: firmware-table API for enumerated tables, the registry for the DSDT,
/// and (if available) ACPICA acpidump.exe for a complete dump including every SSDT instance.</summary>
public static class AcpiDumper
{
    private static readonly uint ProviderAcpi = NativeMethods.Sig("ACPI");

    /// <summary>HKLM\HARDWARE\ACPI\DSDT\&lt;OEM&gt;\&lt;TABLE&gt;\&lt;REV&gt;\(default)</summary>
    public static byte[]? ReadDsdtFromRegistry()
    {
        try
        {
            using var dsdt = Registry.LocalMachine.OpenSubKey(@"HARDWARE\ACPI\DSDT");
            var oem = dsdt?.GetSubKeyNames().FirstOrDefault();
            using var k1 = oem is null ? null : dsdt!.OpenSubKey(oem);
            var tid = k1?.GetSubKeyNames().FirstOrDefault();
            using var k2 = tid is null ? null : k1!.OpenSubKey(tid);
            var rev = k2?.GetSubKeyNames().FirstOrDefault();
            using var k3 = rev is null ? null : k2!.OpenSubKey(rev);
            return k3 is null ? null : k3.GetValueNames().Select(n => k3.GetValue(n)).OfType<byte[]>().FirstOrDefault();
        }
        catch (Exception ex) { Log.Warning(ex, "DSDT registry read failed"); return null; }
    }

    public static AcpiDumpResult Dump(string outDir, string? acpidumpExe)
    {
        Directory.CreateDirectory(outDir);
        var files = new List<string>(); var notes = new List<string>();

        var size = NativeMethods.EnumSystemFirmwareTables(ProviderAcpi, null, 0);
        if (size > 0)
        {
            var buf = new byte[size];
            NativeMethods.EnumSystemFirmwareTables(ProviderAcpi, buf, size);
            var seen = new Dictionary<string, int>();
            for (var i = 0; i + 4 <= buf.Length; i += 4)
            {
                var id = BitConverter.ToUInt32(buf, i);
                var sig = Encoding.ASCII.GetString(buf, i, 4);
                var tsize = NativeMethods.GetSystemFirmwareTable(ProviderAcpi, id, null, 0);
                if (tsize == 0) continue;
                var table = new byte[tsize];
                NativeMethods.GetSystemFirmwareTable(ProviderAcpi, id, table, tsize);
                seen.TryGetValue(sig, out var n); seen[sig] = n + 1;
                if (n > 0 && files.Any(f => File.ReadAllBytes(f).AsSpan().SequenceEqual(table))) continue;
                var path = Path.Combine(outDir, n == 0 ? $"{sig}.aml" : $"{sig}-{n}.aml");
                File.WriteAllBytes(path, table); files.Add(path);
            }
            if (seen.TryGetValue("SSDT", out var c) && c > 1)
                notes.Add($"Windows lists {c} SSDTs but its API returns only the first; use acpidump for every SSDT instance.");
        }

        var dsdt = ReadDsdtFromRegistry();
        if (dsdt != null) { var p = Path.Combine(outDir, "DSDT.aml"); File.WriteAllBytes(p, dsdt); if (!files.Contains(p)) files.Add(p); }
        else notes.Add("DSDT not found in registry.");

        if (!string.IsNullOrEmpty(acpidumpExe) && File.Exists(acpidumpExe))
        {
            var sub = Path.Combine(outDir, "acpidump");
            Directory.CreateDirectory(sub);
            var ok = Run(acpidumpExe, "-b", sub, out var output);
            notes.Add(ok ? "acpidump: complete binary dump written to acpidump\\" : "acpidump failed: " + output);
            if (ok) files.AddRange(Directory.GetFiles(sub));
        }
        Log.Information("ACPI dump: {Count} files to {Dir}", files.Count, outDir);
        return new AcpiDumpResult(outDir, files, notes);
    }

    public static bool Run(string exe, string args, string workDir, out string output)
    {
        var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } output = "timeout"; return false; }
        output = so.Result + se.Result;
        return p.ExitCode == 0;
    }
}

/// <summary>Optional iasl integration: cross-compiles generated .dsl files for verification.</summary>
public static class IaslRunner
{
    public static (bool ok, string output) Compile(string iaslExe, string dslPath)
    {
        var ok = AcpiDumper.Run(iaslExe, $"-ve \"{dslPath}\"", Path.GetDirectoryName(dslPath)!, out var o);
        return (ok, o);
    }
}
