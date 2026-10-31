using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HackWizard.Core.Logging;
using HackWizard.Core.Native;
using Microsoft.Win32;
using Serilog;

namespace HackWizard.Core.Preflight;

public enum CheckStatus { Pass, Warn, Fail, Info }

public sealed record PreflightCheck(string Name, CheckStatus Status, string Detail, string Fix = "");

public sealed class PreflightResult
{
    public List<PreflightCheck> Checks { get; } = new();
    public bool IsUefi { get; set; }
    public bool HasBlockingFailure => Checks.Any(c => c.Status == CheckStatus.Fail);
}

public sealed class PreflightChecker
{
    public const long MinTempBytes = 4L * 1024 * 1024 * 1024;

    public async Task<PreflightResult> RunAsync(CancellationToken ct)
    {
        var r = new PreflightResult();
        r.Checks.Add(CheckArchitecture());
        r.Checks.Add(CheckAdmin());
        r.Checks.Add(CheckWindowsVersion());
        r.Checks.Add(CheckFreeSpace());
        var (uefiCheck, isUefi) = CheckUefi();
        r.IsUefi = isUefi; r.Checks.Add(uefiCheck);
        r.Checks.Add(CheckSecureBoot());
        r.Checks.Add(CheckBitLocker());
        r.Checks.Add(CheckTpm());
        r.Checks.Add(CheckPower());
        r.Checks.Add(await CheckInternetAsync(ct).ConfigureAwait(false));
        foreach (var c in r.Checks) Log.Information("Preflight {Name}: {Status} - {Detail}", c.Name, c.Status, c.Detail);
        return r;
    }

    private static PreflightCheck CheckArchitecture() =>
        NativeMethods.IsArm64Host()
            ? new("Processor architecture", CheckStatus.Fail, "This is an ARM64 PC (e.g. Snapdragon).",
                "macOS for PCs only runs on x86-64 (Intel/AMD) processors. ARM Windows PCs cannot run it — there is no workaround.")
            : new("Processor architecture", CheckStatus.Pass, "x86-64 (Intel/AMD).");

    private static PreflightCheck CheckAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        var admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        return admin ? new("Administrator rights", CheckStatus.Pass, "HackWizard is running elevated.")
                     : new("Administrator rights", CheckStatus.Fail, "Not running as administrator.", "Close HackWizard and start it again, then click 'Yes' on the Windows UAC prompt.");
    }

    private static PreflightCheck CheckWindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        var ok = v.Major >= 10 && v.Build >= 17763;
        return ok ? new("Windows version", CheckStatus.Pass, RuntimeInformation.OSDescription)
                  : new("Windows version", CheckStatus.Fail, RuntimeInformation.OSDescription, "Windows 10 1809 or newer is required.");
    }

    private static PreflightCheck CheckFreeSpace()
    {
        var root = Path.GetPathRoot(AppPaths.Root)!;
        var free = new DriveInfo(root).AvailableFreeSpace;
        var gb = free / 1024d / 1024 / 1024;
        return free >= MinTempBytes
            ? new("Free disk space", CheckStatus.Pass, $"{gb:F1} GB free on {root}")
            : new("Free disk space", CheckStatus.Fail, $"Only {gb:F1} GB free on {root}", "Free up at least 4 GB (macOS Recovery alone is ~1 GB, plus kexts, cache and staging).");
    }

    internal static (PreflightCheck, bool) CheckUefi()
    {
        try
        {
            if (NativeMethods.GetFirmwareType(out var t))
            {
                if (t == NativeMethods.FirmwareType.Uefi) return (new("UEFI firmware", CheckStatus.Pass, "Windows booted in UEFI mode."), true);
                if (t == NativeMethods.FirmwareType.Bios)
                    return (new("UEFI firmware", CheckStatus.Fail, "Windows booted in Legacy/CSM (BIOS) mode.",
                        "OpenCore needs UEFI. If your firmware offers 'UEFI' boot mode, switch to it, reinstall/convert Windows (MBR2GPT) and run HackWizard again. Very old PCs without UEFI are not supported."), false);
            }
        }
        catch (EntryPointNotFoundException) { }

        _ = NativeMethods.GetFirmwareEnvironmentVariableW("", "{00000000-0000-0000-0000-000000000000}", IntPtr.Zero, 0);
        var err = Marshal.GetLastWin32Error();
        if (err != 1) return (new("UEFI firmware", CheckStatus.Pass, $"UEFI detected (firmware variable probe, error {err})."), true);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("bcdedit.exe", "/enum {current}") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000);
            if (o.Contains("winload.efi", StringComparison.OrdinalIgnoreCase)) return (new("UEFI firmware", CheckStatus.Pass, "UEFI detected (bcdedit: winload.efi)."), true);
        }
        catch (Exception ex) { Log.Warning(ex, "bcdedit fallback failed"); }
        return (new("UEFI firmware", CheckStatus.Fail, "Legacy BIOS boot detected.", "Switch firmware to UEFI-only boot mode."), false);
    }

    private static PreflightCheck CheckSecureBoot()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (k?.GetValue("UEFISecureBootEnabled") is int i)
                return i == 1
                    ? new("Secure Boot", CheckStatus.Warn, "Secure Boot is ON.", "You must turn Secure Boot OFF in the firmware setup before booting the USB. The BIOS Guide step shows where.")
                    : new("Secure Boot", CheckStatus.Pass, "Secure Boot is OFF.");
        }
        catch (Exception ex) { Log.Warning(ex, "Secure Boot query failed"); }
        return new("Secure Boot", CheckStatus.Info, "Secure Boot state unknown (not supported on this firmware).");
    }

    private static PreflightCheck CheckBitLocker()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            var on = s.Get().Cast<ManagementObject>().Where(m => Convert.ToInt32(m["ProtectionStatus"] ?? 0) == 1).Select(m => m["DriveLetter"]?.ToString()).ToList();
            if (on.Count > 0)
                return new("BitLocker / Device Encryption", CheckStatus.Warn, $"Protection is ON for: {string.Join(", ", on)}",
                    "Installing macOS erases this disk. Back up your files and save your recovery key (https://aka.ms/myrecoverykey) first. Changing BIOS settings may also trigger a recovery-key prompt in Windows.");
            return new("BitLocker / Device Encryption", CheckStatus.Pass, "No protected volumes.");
        }
        catch (Exception ex) { Log.Information(ex, "BitLocker WMI not available"); return new("BitLocker / Device Encryption", CheckStatus.Info, "Status unavailable (Home edition or WMI provider missing)."); }
    }

    private static PreflightCheck CheckTpm()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftTpm", "SELECT SpecVersion FROM Win32_Tpm");
            var tpm = s.Get().Cast<ManagementObject>().FirstOrDefault();
            return tpm is null
                ? new("TPM", CheckStatus.Info, "No TPM reported. (macOS does not use it.)")
                : new("TPM", CheckStatus.Info, $"TPM present (spec {tpm["SpecVersion"]}). macOS ignores it; no change needed.");
        }
        catch { return new("TPM", CheckStatus.Info, "TPM status unavailable."); }
    }

    private static PreflightCheck CheckPower()
    {
        if (NativeMethods.GetSystemPowerStatus(out var ps))
        {
            if (ps.ACLineStatus == 0)
                return new("Power", CheckStatus.Warn, $"Running on battery ({ps.BatteryLifePercent}%).", "Plug in the charger. Do not let the PC sleep or lose power while writing the USB drive or installing macOS.");
            return new("Power", CheckStatus.Pass, ps.BatteryFlag == 128 ? "Desktop / no battery." : "AC power connected.");
        }
        return new("Power", CheckStatus.Info, "Power status unknown.");
    }

    private static async Task<PreflightCheck> CheckInternetAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HackWizard/1.1");
            using var r1 = await http.GetAsync("https://api.github.com/rate_limit", ct).ConfigureAwait(false);
            using var r2 = await http.GetAsync("http://osrecovery.apple.com/", ct).ConfigureAwait(false);
            return new("Internet", CheckStatus.Pass, $"GitHub API: {(int)r1.StatusCode}, Apple recovery server: {(int)r2.StatusCode}");
        }
        catch (Exception ex)
        {
            return new("Internet", CheckStatus.Warn, $"Connectivity problem: {ex.Message}", "Connect to the internet, or enable 'Use cached files (offline)' on the Downloads step if you already have a complete cache.");
        }
    }
}
