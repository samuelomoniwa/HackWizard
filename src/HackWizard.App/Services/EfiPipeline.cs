using System.Net;
using System.Net.Http;
using System.Text;
using HackWizard.Core.Acpi;
using HackWizard.Core.Efi;
using HackWizard.Core.Logging;
using HackWizard.Core.Plist;
using HackWizard.Core.Reports;
using HackWizard.Core.Rules;
using HackWizard.Downloads;
using Serilog;

namespace HackWizard.App.Services;

public sealed record KextRequest(string BundleName, KbSource Source);

public sealed class DownloadSet
{
    public required ResolvedArtifact OpenCore { get; init; }
    public ResolvedArtifact? AmdVanilla { get; init; }
    public ResolvedArtifact? HfsPlus { get; init; }
    public List<(KextRequest Request, ResolvedArtifact Artifact)> Kexts { get; } = new();
    public List<ResolvedArtifact> Utilities { get; } = new();
    public ResolvedArtifact? Iasl { get; set; }
    public RecoveryResult? Recovery { get; set; }
    public required DownloadManager Manager { get; init; }
}

public sealed class BuildResult
{
    public required string StagingDir { get; init; }
    public required string ConfigPath { get; init; }
    public required string ReleaseConfigPath { get; init; }
    public required OcValidateResult Validation { get; init; }
    public required OcValidateResult ReleaseValidation { get; init; }
    public required IReadOnlyList<string> Diff { get; init; }
    public required SmbiosIdentity Identity { get; init; }
    public required IReadOnlyList<KextInfo> Kexts { get; init; }
    public List<string> Notes { get; } = new();
    public bool Ok => Validation.Ok && ReleaseValidation.Ok;
}

/// <summary>Orchestrates downloads, EFI assembly, validation and USB staging. Everything hardware-specific comes from
/// the rules result (CompatibilityResult); nothing here is tied to a particular PC.</summary>
public static class EfiPipeline
{
    public static HttpClient CreateHttp(bool cookies = true)
    {
        var handler = new SocketsHttpHandler { UseCookies = cookies, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, ConnectTimeout = TimeSpan.FromSeconds(30) };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        if (cookies) http.DefaultRequestHeaders.UserAgent.ParseAdd("HackWizard/1.1 (+open source)");
        return http;
    }

    /// <summary>Resolves rule kext names ('@Variant', 'Name|sourceId') to download sources for the chosen macOS.</summary>
    public static List<KextRequest> ResolveKexts(KnowledgeBase kb, CompatibilityResult c, KbMacosVersion macos)
    {
        var list = new List<KextRequest>();
        foreach (var name in c.EffectiveKexts)
        {
            IEnumerable<string> expanded = name.StartsWith('@')
                ? macos.KextVariants.TryGetValue(name[1..], out var v) ? v : throw new InvalidDataException($"macOS {macos.Id} has no kext variant '{name[1..]}'.")
                : new[] { name };
            foreach (var e in expanded)
            {
                var src = kb.SourceProviding(e) ?? throw new InvalidDataException($"No download source provides '{e}'.");
                var bundle = e.Split('|')[0];
                if (c.ExcludedKexts.Contains(bundle)) continue;
                if (list.All(k => k.BundleName != bundle)) list.Add(new KextRequest(bundle, src));
            }
        }
        return list;
    }

    public static async Task<DownloadSet> DownloadAllAsync(WizardContext ctx, bool offline, Action<string, DownloadProgress> report, CancellationToken ct)
    {
        var kb = ctx.Kb; var c = ctx.Compat!; var macos = ctx.SelectedMacos!;
        var dm = new DownloadManager(CreateHttp(), AppPaths.Cache) { Offline = offline };
        IProgress<DownloadProgress> P(string id) => new Progress<DownloadProgress>(p => report(id, p));

        var set = new DownloadSet
        {
            OpenCore = await dm.GetAsync(kb.Source("opencore"), P("opencore"), ct), Manager = dm,
            AmdVanilla = c.ConfigPatches.Any(p => p.Key == "$amdVanilla") ? await dm.GetAsync(kb.Source("amdvanilla"), P("amdvanilla"), ct) : null,
            HfsPlus = await TryGet(dm, kb.Source("hfsplus"), P("hfsplus"), ct)
        };
        var kexts = ResolveKexts(kb, c, macos);
        foreach (var src in kexts.Select(k => k.Source).Distinct())
        {
            var art = await dm.GetAsync(src, P(src.Id), ct);
            foreach (var k in kexts.Where(x => x.Source == src)) set.Kexts.Add((k, art));
        }

        // Audio layout-ids for codecs without a curated list: read them from AppleALC's own database.
        if (c.AudioLayoutId is null && c.AudioCodecKey.Length > 0)
        {
            var alc = await TryGet(dm, kb.Source("applealc-src"), P("applealc-src"), ct);
            var found = alc?.ExtractedDir is { } dir ? AppleAlcLayouts.Find(dir, c.AudioCodecKey) : null;
            ApplyAudioLayouts(c, found);
        }

        var utilIds = new List<string> { "usbtoolbox-win", "usbtoolbox-mac", "usbtoolbox-kext" };
        if (kexts.Any(k => k.BundleName == "itlwm.kext")) utilIds.Add("heliport");
        foreach (var id in utilIds) if (await TryGet(dm, kb.Source(id), P(id), ct) is { } u) set.Utilities.Add(u);
        var iasl = kb.Source("iasl");
        if (!string.IsNullOrWhiteSpace(iasl.Url)) set.Iasl = await TryGet(dm, iasl, P("iasl"), ct);

        var recDir = Path.Combine(AppPaths.Cache, "recovery", macos.Id);
        if (offline)
        {
            var dmg = Directory.Exists(recDir) ? Directory.GetFiles(recDir, "*.dmg").FirstOrDefault() : null;
            var cnk = Directory.Exists(recDir) ? Directory.GetFiles(recDir, "*.chunklist").FirstOrDefault() : null;
            if (dmg is null || cnk is null) throw new DownloadException("macOS Recovery is not cached.", "Turn off offline mode to download it.");
            var n = Chunklist.Verify(dmg, cnk);
            set.Recovery = new RecoveryResult(dmg, cnk, new RecoveryImageInfo("cached", "", "", "", "", "", ""), n);
            report("recovery", new DownloadProgress("macOS Recovery", 1, 1, $"Cached, {n} chunks verified"));
        }
        else
        {
            set.Recovery = await new MacRecoveryClient(CreateHttp(cookies: false)).DownloadAsync(macos.BoardId, macos.Mlb, macos.OsType, recDir, P("recovery"), ct);
        }
        dm.WriteLog(Path.Combine(AppPaths.Logs, "download-manifest.json"));
        return set;
    }

    public static void ApplyAudioLayouts(CompatibilityResult c, AppleAlcLayouts.Result? found)
    {
        if (found is { Layouts.Count: > 0 })
        {
            c.AudioLayoutCandidates.Clear(); c.AudioLayoutCandidates.AddRange(found.Layouts);
            c.AudioLayoutId = found.Layouts[0];
            c.Warnings.Add($"Audio: {found.CodecName} — using layout-id {found.Layouts[0]}; alternatives: {string.Join(", ", found.Layouts.Skip(1))}.");
        }
        else
        {
            c.AudioLayoutId = 1; c.AudioLayoutCandidates.Add(1);
            c.Warnings.Add($"Audio: codec {c.AudioCodecKey} not found in AppleALC's database; layout-id 1 is a guess. Audio may not work.");
        }
    }

    private static async Task<ResolvedArtifact?> TryGet(DownloadManager dm, KbSource s, IProgress<DownloadProgress> p, CancellationToken ct)
    {
        try { return await dm.GetAsync(s, p, ct); }
        catch (DownloadException ex) when (s.Optional || s.Kind is "utility" or "tool" or "driver" or "data")
        {
            Log.Warning(ex, "Optional artifact {Id} unavailable", s.Id);
            p.Report(new DownloadProgress(s.Id, 0, null, "Skipped: " + ex.Message));
            return null;
        }
    }

    public static BuildResult Build(WizardContext ctx, ISerialGenerator? serialOverride = null)
    {
        var d = ctx.Downloads ?? throw new InvalidOperationException("Downloads missing.");
        var hw = ctx.Hardware!; var c = ctx.Compat!;
        if (string.IsNullOrEmpty(c.Smbios)) throw new InvalidOperationException("No SMBIOS model was selected for this hardware.");
        var ocRoot = d.OpenCore.ExtractedDir ?? throw new InvalidOperationException("OpenCore package not extracted.");
        var staging = Path.Combine(AppPaths.Work, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        var efi = Path.Combine(staging, "EFI"); var oc = Path.Combine(efi, "OC");
        foreach (var dir in new[] { Path.Combine(efi, "BOOT"), Path.Combine(oc, "ACPI"), Path.Combine(oc, "Drivers"), Path.Combine(oc, "Kexts"), Path.Combine(oc, "Tools"), Path.Combine(oc, "Resources") })
            Directory.CreateDirectory(dir);

        var x64 = Path.Combine(ocRoot, "X64", "EFI");
        File.Copy(Path.Combine(x64, "BOOT", "BOOTx64.efi"), Path.Combine(efi, "BOOT", "BOOTx64.efi"));
        File.Copy(Path.Combine(x64, "OC", "OpenCore.efi"), Path.Combine(oc, "OpenCore.efi"));
        var drivers = new List<string> { "OpenRuntime.efi" };
        File.Copy(Path.Combine(x64, "OC", "Drivers", "OpenRuntime.efi"), Path.Combine(oc, "Drivers", "OpenRuntime.efi"));
        var notes = new List<string>();
        if (d.HfsPlus is { } hfs) { File.Copy(hfs.LocalPath, Path.Combine(oc, "Drivers", "HfsPlus.efi")); drivers.Add("HfsPlus.efi"); }
        else if (File.Exists(Path.Combine(x64, "OC", "Drivers", "OpenHfsPlus.efi")))
        {
            File.Copy(Path.Combine(x64, "OC", "Drivers", "OpenHfsPlus.efi"), Path.Combine(oc, "Drivers", "OpenHfsPlus.efi"));
            drivers.Add("OpenHfsPlus.efi"); notes.Add("HfsPlus.efi unavailable; using OpenCorePkg's OpenHfsPlus.efi.");
        }
        else throw new InvalidOperationException("No HFS+ driver available (HfsPlus.efi or OpenHfsPlus.efi). The recovery image cannot be booted without it.");
        File.Copy(Path.Combine(x64, "OC", "Drivers", "ResetNvramEntry.efi"), Path.Combine(oc, "Drivers", "ResetNvramEntry.efi"));
        drivers.Add("ResetNvramEntry.efi");

        var kextInfos = new List<KextInfo>();
        foreach (var (req, art) in d.Kexts)
        {
            var src = DownloadManager.FindInExtracted(art.ExtractedDir ?? throw new InvalidOperationException($"{art.SourceId} is not an archive."), req.BundleName);
            var dst = Path.Combine(oc, "Kexts", req.BundleName);
            CopyDir(src, dst);
            kextInfos.AddRange(KextInfo.ReadWithPlugins(dst, req.BundleName));
        }
        var ordered = KextOrderer.Order(kextInfos);

        foreach (var s in ctx.Ssdts) File.WriteAllBytes(Path.Combine(oc, "ACPI", s.Name + ".aml"), s.Aml);

        var sample = PlistReader.ReadFile(Path.Combine(ocRoot, "Docs", "Sample.plist")) as PDict ?? throw new PlistException("Sample.plist invalid.");
        PArray? amd = d.AmdVanilla is { } av ? AmdVanillaPatcher.LoadAndPatch(PlistReader.ReadFile(av.LocalPath), hw.Cpu.PhysicalCores) : null;
        var gen = serialOverride ?? new MacSerialGenerator(Path.Combine(ocRoot, "Utilities", "macserial", "macserial.exe"));
        var identity = gen.Generate(c.Smbios);
        var outputs = ConfigBuilder.Build(new ConfigInputs
        {
            SamplePlist = sample, Compat = c, OrderedKexts = ordered, SsdtFiles = ctx.Ssdts.Select(s => s.Name + ".aml").ToList(),
            Drivers = drivers, AmdPatches = amd, Identity = identity, Rom = ConfigBuilder.RomFromMac(hw.EthernetMac)
        });
        var cfgPath = Path.Combine(oc, "config.plist");
        var relPath = Path.Combine(oc, "config-release.plist");
        PlistWriter.WriteFile(outputs.Debug, cfgPath);
        PlistWriter.WriteFile(outputs.Release, relPath);

        var ocvalidate = Path.Combine(ocRoot, "Utilities", "ocvalidate", "ocvalidate.exe");
        var result = new BuildResult
        {
            StagingDir = staging, ConfigPath = cfgPath, ReleaseConfigPath = relPath,
            Validation = OcValidate.Run(ocvalidate, cfgPath), ReleaseValidation = OcValidate.Run(ocvalidate, relPath),
            Diff = outputs.DiffVsSample, Identity = identity, Kexts = ordered
        };
        result.Notes.AddRange(notes);
        if (amd != null) result.Notes.Add($"AMD_Vanilla: {AmdVanillaPatcher.CountCorePatches(amd)} core-count patches set to {hw.Cpu.PhysicalCores} cores.");
        if (c.AudioLayoutId is { } lay) result.Notes.Add($"Audio layout-id {lay} (candidates: {string.Join(", ", c.AudioLayoutCandidates)}).");

        if (d.Iasl?.ExtractedDir is { } iaslDir && Directory.GetFiles(iaslDir, "iasl.exe", SearchOption.AllDirectories).FirstOrDefault() is { } iasl)
        {
            var dslDir = AppPaths.Ensure(Path.Combine(AppPaths.Work, "dsl-check"));
            foreach (var s in ctx.Ssdts)
            {
                var p = Path.Combine(dslDir, s.Name + ".dsl"); File.WriteAllText(p, s.Dsl);
                var (ok, o) = IaslRunner.Compile(iasl, p);
                result.Notes.Add($"iasl {s.Name}: {(ok ? "OK" : "FAILED")}");
                if (!ok) Log.Warning("iasl {Name}: {Out}", s.Name, o);
            }
        }
        if (!result.Ok) return result;

        if (d.Recovery is { } rec)
        {
            var recDir = AppPaths.Ensure(Path.Combine(staging, "com.apple.recovery.boot"));
            File.Copy(rec.DmgPath, Path.Combine(recDir, Path.GetFileName(rec.DmgPath)));
            File.Copy(rec.ChunklistPath, Path.Combine(recDir, Path.GetFileName(rec.ChunklistPath)));
        }
        WriteUtilities(ctx, d, result, ocRoot, ocvalidate);
        return result;
    }

    private static void WriteUtilities(WizardContext ctx, DownloadSet d, BuildResult r, string ocRoot, string ocvalidate)
    {
        var u = AppPaths.Ensure(Path.Combine(r.StagingDir, "Utilities"));
        ReportWriter.WriteJson(ctx.Hardware!, Path.Combine(u, "hardware-report.json"));
        ReportWriter.WriteHtml(ctx.Hardware!, ctx.Compat, Path.Combine(u, "hardware-report.html"));
        var guide = BiosGuideGenerator.Build(ctx.Kb, ctx.Hardware!, ctx.Compat);
        File.WriteAllText(Path.Combine(u, "BIOS-Guide.html"), BiosGuideGenerator.ToHtml(guide), new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(u, "BIOS-Guide.pdf"), SimplePdf.FromBiosGuide(guide));
        File.WriteAllText(Path.Combine(u, "Post-Install-Guide.html"), PostInstallGuide.ToHtml(ctx.Compat!, ctx.SelectedMacos!.Name, r.Identity.Serial), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(u, "Copy-EFI-To-Internal.command"), PostInstallGuide.CopyEfiScript.Replace("\r\n", "\n"), new UTF8Encoding(false));
        var cfgBk = AppPaths.Ensure(Path.Combine(u, "config-backup"));
        File.Copy(r.ConfigPath, Path.Combine(cfgBk, "config.plist"));
        File.Copy(r.ReleaseConfigPath, Path.Combine(cfgBk, "config-release.plist"));
        File.WriteAllText(Path.Combine(cfgBk, "diff-vs-Sample.txt"), PlistDiff.ToText(r.Diff));
        var ocv = AppPaths.Ensure(Path.Combine(u, "ocvalidate"));
        File.Copy(ocvalidate, Path.Combine(ocv, "ocvalidate.exe"));
        var macOcv = Path.Combine(ocRoot, "Utilities", "ocvalidate", "ocvalidate");
        if (File.Exists(macOcv)) File.Copy(macOcv, Path.Combine(ocv, "ocvalidate"));
        var ssdtSrc = AppPaths.Ensure(Path.Combine(u, "SSDT-Sources"));
        foreach (var s in ctx.Ssdts) File.WriteAllText(Path.Combine(ssdtSrc, s.Name + ".dsl"), s.Dsl);
        if (ctx.AcpiDump is { } dump && Directory.Exists(dump.Directory)) CopyDir(dump.Directory, Path.Combine(u, "ACPI-Dump"));
        var usbtb = AppPaths.Ensure(Path.Combine(u, "USBToolBox"));
        var post = AppPaths.Ensure(Path.Combine(u, "PostInstall"));
        foreach (var a in d.Utilities)
        {
            var target = a.SourceId switch
            {
                "usbtoolbox-win" => Path.Combine(usbtb, "USBToolBox-Windows.exe"),
                "usbtoolbox-mac" => Path.Combine(usbtb, "USBToolBox-macOS.zip"),
                "usbtoolbox-kext" => Path.Combine(usbtb, a.AssetName),
                _ => Path.Combine(post, a.AssetName)
            };
            File.Copy(a.LocalPath, target, true);
        }
        d.Manager.WriteLog(Path.Combine(u, "download-manifest.json"));
        File.WriteAllText(Path.Combine(u, "README.txt"),
            "HackWizard USB\r\n\r\nEFI\\                       OpenCore bootloader + config\r\ncom.apple.recovery.boot\\   macOS Recovery (online installer: needs a wired connection)\r\n" +
            "Utilities\\BIOS-Guide.*     firmware settings for this PC\r\nUtilities\\Post-Install-Guide.html  what to do after installing\r\n" +
            $"\r\nSMBIOS {r.Identity.Model}  Serial {r.Identity.Serial}  (check it is NOT valid at checkcoverage.apple.com)\r\n");
    }

    public static void CopyLogs(string stagingDir)
    {
        var dst = AppPaths.Ensure(Path.Combine(stagingDir, "Utilities", "logs"));
        foreach (var f in Directory.GetFiles(AppPaths.Logs))
        {
            try
            {
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var o = File.Create(Path.Combine(dst, Path.GetFileName(f)));
                src.CopyTo(o);
            }
            catch (IOException ex) { Log.Warning(ex, "Could not copy log {File}", f); }
        }
    }

    public static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var dir in Directory.GetDirectories(src)) CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
}
