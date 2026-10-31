using System.Text;
using System.Text.Json;
using HackWizard.Core.Plist;
using HackWizard.Core.Rules;

namespace HackWizard.Core.Efi;

public sealed class ConfigInputs
{
    public required PDict SamplePlist { get; init; }
    public required CompatibilityResult Compat { get; init; }
    public required IReadOnlyList<KextInfo> OrderedKexts { get; init; }
    public required IReadOnlyList<string> SsdtFiles { get; init; }
    public required IReadOnlyList<string> Drivers { get; init; }
    public PArray? AmdPatches { get; init; }
    public required SmbiosIdentity Identity { get; init; }
    public required byte[] Rom { get; init; }
}

public sealed class ConfigOutputs
{
    public required PDict Debug { get; init; }
    public required PDict Release { get; init; }
    public required IReadOnlyList<string> DiffVsSample { get; init; }
}

/// <summary>Builds config.plist from the matching OpenCorePkg Sample.plist (never a prebuilt EFI). All hardware-specific
/// values come from the rules result; this class only knows OpenCore's structure.</summary>
public static class ConfigBuilder
{
    public const string AppleVendorGuid = "7C436110-AB2A-4BBB-A880-FE41995C9F82";
    public static readonly string[] DebugBootArgs = { "-v", "keepsyms=1", "debug=0x100" };

    public static ConfigOutputs Build(ConfigInputs i)
    {
        var cfg = (PDict)i.SamplePlist.Clone();
        var c = i.Compat;
        if (string.IsNullOrEmpty(i.Identity.Model)) throw new InvalidOperationException("No SMBIOS model selected.");

        // ACPI
        var acpi = cfg.Dict("ACPI");
        var acpiAddTemplate = FirstTemplate(acpi.Array("Add"), "ACPI.Add");
        acpi["Add"] = new PArray(i.SsdtFiles.Select(f => ((PDict)acpiAddTemplate.Clone()).Set("Comment", Path.GetFileNameWithoutExtension(f)).Set("Enabled", true).Set("Path", f)));
        var acpiPatchTemplate = acpi.Array("Patch").OfType<PDict>().FirstOrDefault();
        acpi["Delete"] = new PArray();
        acpi["Patch"] = new PArray();
        if (i.SsdtFiles.Contains("SSDT-XOSI.aml"))
        {
            if (acpiPatchTemplate is null) throw new PlistException("Sample.plist has no ACPI.Patch template entry.");
            var p = (PDict)acpiPatchTemplate.Clone();
            p.Set("Comment", "Rename _OSI to XOSI (pairs with SSDT-XOSI)").Set("Count", 0L).Set("Enabled", true)
             .Set("Find", Encoding.ASCII.GetBytes("_OSI")).Set("Replace", Encoding.ASCII.GetBytes("XOSI"))
             .Set("Limit", 0L).Set("Mask", Array.Empty<byte>()).Set("ReplaceMask", Array.Empty<byte>()).Set("Skip", 0L)
             .Set("TableLength", 0L).Set("TableSignature", Encoding.ASCII.GetBytes("DSDT"));
            if (p.ContainsKey("OemTableId")) p.Set("OemTableId", Array.Empty<byte>());
            if (p.ContainsKey("Base")) p.Set("Base", "");
            if (p.ContainsKey("BaseSkip")) p.Set("BaseSkip", 0L);
            acpi.Array("Patch").Add(p);
        }

        // Booter
        var booter = cfg.Dict("Booter");
        booter["MmioWhitelist"] = new PArray();
        booter["Patch"] = new PArray();

        // DeviceProperties
        var dpAdd = new PDict();
        foreach (var (path, props) in c.DeviceProperties.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var d = new PDict();
            foreach (var (k, v) in props.OrderBy(k => k.Key, StringComparer.Ordinal)) d[k] = FromJson(v, null);
            dpAdd[path] = d;
        }
        var bootArgs = new List<string>(c.BootArgs);
        if (c.AudioLayoutId is { } layout)
        {
            if (!string.IsNullOrEmpty(c.AudioDevicePath))
            {
                if (dpAdd[c.AudioDevicePath] is not PDict ad) dpAdd[c.AudioDevicePath] = ad = new PDict();
                ad.Set("layout-id", BitConverter.GetBytes(layout));
            }
            else RulesEngine.AddBootArg(bootArgs, $"alcid={layout}");
        }
        cfg.Dict("DeviceProperties")["Add"] = dpAdd;
        cfg.Dict("DeviceProperties")["Delete"] = new PDict();

        // Kernel
        var kernel = cfg.Dict("Kernel");
        var kextTemplate = FirstTemplate(kernel.Array("Add"), "Kernel.Add");
        kernel["Add"] = new PArray(i.OrderedKexts.Select(k =>
        {
            var e = (PDict)kextTemplate.Clone();
            e.Set("Arch", "Any").Set("BundlePath", k.BundlePath).Set("Comment", string.IsNullOrEmpty(k.Comment) ? k.Name : k.Comment)
             .Set("Enabled", k.Enabled).Set("ExecutablePath", k.ExecutablePath).Set("MaxKernel", "").Set("MinKernel", "").Set("PlistPath", k.PlistPath);
            return (PNode)e;
        }));
        kernel["Block"] = new PArray();
        kernel["Force"] = new PArray();
        kernel["Patch"] = new PArray();
        if (c.ConfigPatches.Any(p => p.Key == "$amdVanilla" && p.Value.ValueKind == JsonValueKind.True))
            kernel["Patch"] = (i.AmdPatches ?? throw new InvalidOperationException("Rules require AMD_Vanilla patches but none were supplied.")).Clone();

        // Misc
        var misc = cfg.Dict("Misc");
        misc["Entries"] = new PArray();
        misc["Tools"] = new PArray();

        // Rule config patches (ascending priority; later wins)
        foreach (var (path, value) in c.ConfigPatches)
        {
            if (path.StartsWith('$')) continue;
            PlistPath.Set(cfg, path, FromJson(value, PlistPath.Get(cfg, path)), mustExist: true);
        }

        // NVRAM
        var apple = cfg.Dict("NVRAM").Dict("Add")[AppleVendorGuid] as PDict ?? throw new PlistException($"NVRAM.Add.{AppleVendorGuid} missing in Sample.plist.");
        apple.Set("boot-args", string.Join(' ', bootArgs));
        apple.Set("csr-active-config", new byte[] { 0, 0, 0, 0 });
        apple["prev-lang:kbd"] = apple["prev-lang:kbd"] is PString ? new PString("en-US:0") : new PData(Encoding.ASCII.GetBytes("en-US:0"));
        if (cfg.Dict("NVRAM").Dict("Delete")[AppleVendorGuid] is PArray del && !del.OfType<PString>().Any(s => s.Value == "boot-args")) del.Add(new PString("boot-args"));

        // PlatformInfo
        cfg.Dict("PlatformInfo").Dict("Generic")
           .Set("SystemProductName", i.Identity.Model).Set("SystemSerialNumber", i.Identity.Serial)
           .Set("MLB", i.Identity.Mlb).Set("SystemUUID", i.Identity.SystemUuid).Set("ROM", i.Rom);

        // UEFI drivers
        var uefi = cfg.Dict("UEFI");
        var drvTemplate = FirstTemplate(uefi.Array("Drivers"), "UEFI.Drivers");
        uefi["Drivers"] = new PArray(i.Drivers.Select(d =>
        {
            var e = (PDict)drvTemplate.Clone();
            e.Set("Path", d).Set("Enabled", true).Set("Comment", d);
            if (e.ContainsKey("Arguments")) e.Set("Arguments", "");
            if (e.ContainsKey("LoadEarly")) e.Set("LoadEarly", false);
            return (PNode)e;
        }));

        return new ConfigOutputs { Debug = cfg, Release = MakeRelease(cfg), DiffVsSample = PlistDiff.Diff(i.SamplePlist, cfg) };
    }

    /// <summary>"Release" toggle: turns off verbose/debug settings once the system boots reliably.</summary>
    public static PDict MakeRelease(PDict debugCfg)
    {
        var r = (PDict)debugCfg.Clone();
        PlistPath.Set(r, "Misc.Debug.AppleDebug", new PBool(false));
        PlistPath.Set(r, "Misc.Debug.ApplePanic", new PBool(false));
        PlistPath.Set(r, "Misc.Debug.Target", new PInteger(3));
        var apple = r.Dict("NVRAM").Dict("Add").Dict(AppleVendorGuid);
        apple.Set("boot-args", string.Join(' ', (apple.Str("boot-args") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(a => !DebugBootArgs.Contains(a))));
        return r;
    }

    private static PDict FirstTemplate(PArray arr, string what) =>
        arr.OfType<PDict>().FirstOrDefault() ?? throw new PlistException($"Sample.plist {what} has no template entry (OpenCore schema changed?).");

    /// <summary>JSON rule value → plist node. When the target exists, its Sample type (data/integer/real) is kept.</summary>
    public static PNode FromJson(JsonElement v, PNode? existing)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return new PBool(true);
            case JsonValueKind.False: return new PBool(false);
            case JsonValueKind.Number: return existing is PReal ? new PReal(v.GetDouble()) : new PInteger(v.GetInt64());
            case JsonValueKind.String: return existing is PData ? PData.FromHex(v.GetString()!) : new PString(v.GetString()!);
            case JsonValueKind.Object:
                if (v.TryGetProperty("data", out var hex)) return PData.FromHex(hex.GetString() ?? "");
                if (v.TryGetProperty("int32le", out var n)) return new PData(BitConverter.GetBytes(n.GetInt32()));
                break;
        }
        throw new PlistException($"Unsupported rule value: {v.GetRawText()}");
    }

    public static byte[] RomFromMac(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length == 12) return Convert.FromHexString(hex);
        var b = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        b[0] = (byte)((b[0] & 0xFE) | 0x02);
        return b;
    }
}
