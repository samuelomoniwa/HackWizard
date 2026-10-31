using System.Text;
using HackWizard.Core.Acpi;
using HackWizard.Core.Efi;
using HackWizard.Core.Plist;
using HackWizard.Core.Rules;
using Xunit;

namespace HackWizard.Tests;

public class ConfigBuilderTests
{
    internal static ConfigOutputs Build(string profile, PDict? sample = null, PNode? patches = null, string macos = "")
    {
        var kb = TestData.Kb();
        var hw = TestData.Profile(profile);
        var compat = TestData.Eval(hw);
        Assert.False(compat.IsBlocked, string.Join("\n", compat.BlockingReasons));
        if (compat.AudioLayoutId is null && compat.AudioCodecKey.Length > 0)
        {
            var found = AppleAlcLayouts.Find(TestData.AppleAlcFixture, compat.AudioCodecKey);
            compat.AudioLayoutId = found?.Layouts[0] ?? 1;
        }
        var version = kb.Version(macos.Length > 0 ? macos : compat.RecommendedMacos);
        var ssdts = new SsdtGenerator().Generate(compat.Ssdts, hw);
        return ConfigBuilder.Build(new ConfigInputs
        {
            SamplePlist = sample ?? TestData.Sample(),
            Compat = compat,
            OrderedKexts = KextOrderer.Order(TestData.KextsFor(compat, version)),
            SsdtFiles = ssdts.Select(s => s.Name + ".aml").ToList(),
            Drivers = new[] { "OpenRuntime.efi", "HfsPlus.efi", "ResetNvramEntry.efi" },
            AmdPatches = compat.ConfigPatches.Any(p => p.Key == "$amdVanilla") ? AmdVanillaPatcher.LoadAndPatch(patches ?? TestData.Patches(), hw.Cpu.PhysicalCores) : null,
            Identity = TestData.FixedIdentity with { Model = compat.Smbios },
            Rom = ConfigBuilder.RomFromMac(hw.EthernetMac)
        });
    }

    private static PNode? At(PDict d, string path) => PlistPath.Get(d, path);
    private static bool Bool(PDict d, string path) => ((PBool)At(d, path)!).Value;

    public static IEnumerable<object[]> GoldenProfiles => new[] { "t495", "intel-coffee-desktop", "intel-comet-u-laptop", "intel-alder-desktop-rx6600", "intel-haswell-laptop", "amd-ryzen-desktop-rx580" }.Select(p => new object[] { p });

    /// <summary>Snapshot of the full config.plist per profile. Approve intended changes with build.ps1 -ApproveSnapshots.</summary>
    [Theory, MemberData(nameof(GoldenProfiles))]
    public void GoldenSnapshot(string profile)
    {
        var actual = PlistWriter.ToXml(Build(profile).Debug);
        var projectDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (projectDir != null && !File.Exists(Path.Combine(projectDir.FullName, "HackWizard.Tests.csproj"))) projectDir = projectDir.Parent;
        Assert.NotNull(projectDir);
        var golden = Path.Combine(projectDir!.FullName, "Golden", profile + ".config.plist");
        if (Environment.GetEnvironmentVariable("HACKWIZARD_APPROVE") == "1") { Directory.CreateDirectory(Path.GetDirectoryName(golden)!); File.WriteAllText(golden, actual, new UTF8Encoding(false)); }
        Assert.True(File.Exists(golden), $"Golden file missing: review the output and approve with build.ps1 -ApproveSnapshots ({golden}).");
        if (File.ReadAllText(golden).Replace("\r\n", "\n") != actual)
        {
            File.WriteAllText(golden + ".received", actual, new UTF8Encoding(false));
            Assert.Fail($"config.plist differs from the golden file. Compare {golden}.received with {golden}; re-approve if intended.");
        }
    }

    [Fact]
    public void T495_AmdQuirks_And_Basics()
    {
        var c = Build("t495").Debug;
        Assert.True(Bool(c, "Kernel.Quirks.ProvideCurrentCpuInfo"));
        Assert.True(Bool(c, "Kernel.Quirks.PanicNoKextDump"));
        Assert.True(Bool(c, "Kernel.Quirks.PowerTimeoutKernelPanic"));
        Assert.True(Bool(c, "Kernel.Emulate.DummyPowerManagement"));
        Assert.False(Bool(c, "Kernel.Quirks.XhciPortLimit"));
        Assert.False(Bool(c, "Booter.Quirks.EnableWriteUnprotector"));
        Assert.True(Bool(c, "Booter.Quirks.RebuildAppleMemoryMap"));
        Assert.Equal("Disabled", ((PString)At(c, "Misc.Security.SecureBootModel")!).Value);
        Assert.Equal(0, ((PInteger)At(c, "Misc.Security.ScanPolicy")!).Value);
        Assert.Equal(67, ((PInteger)At(c, "Misc.Debug.Target")!).Value);
        Assert.Equal("MacBookPro16,2", c.Dict("PlatformInfo").Dict("Generic").Str("SystemProductName"));
        Assert.Equal(Convert.FromHexString("8C1645AABBCC"), c.Dict("PlatformInfo").Dict("Generic").Get<PData>("ROM").Value);
        var audio = c.Dict("DeviceProperties").Dict("Add").Dict("PciRoot(0x0)/Pci(0x8,0x1)/Pci(0x0,0x6)");
        Assert.Equal(new byte[] { 11, 0, 0, 0 }, audio.Get<PData>("layout-id").Value);
        var cores = c.Dict("Kernel").Array("Patch").OfType<PDict>().Where(p => p.Str("Comment")!.Contains("cpuid_cores_per_package")).ToList();
        Assert.Equal(3, cores.Count);
        Assert.All(cores, p => Assert.Equal(4, p.Get<PData>("Replace").Value[1]));
    }

    [Fact]
    public void CoffeeDesktop_IntelQuirks_Igpu_Ssdts()
    {
        var c = Build("intel-coffee-desktop").Debug;
        Assert.True(Bool(c, "Kernel.Quirks.AppleXcpmCfgLock"));
        Assert.True(Bool(c, "Kernel.Quirks.DisableIoMapper"));
        Assert.False(Bool(c, "Kernel.Quirks.ProvideCurrentCpuInfo"));
        Assert.False(Bool(c, "Kernel.Emulate.DummyPowerManagement"));
        Assert.True(Bool(c, "Booter.Quirks.EnableWriteUnprotector"));
        Assert.True(Bool(c, "Booter.Quirks.DevirtualiseMmio"));
        Assert.Equal(0, c.Dict("Kernel").Array("Patch").Count); // no AMD patches on Intel
        Assert.Equal("iMac19,1", c.Dict("PlatformInfo").Dict("Generic").Str("SystemProductName"));
        Assert.Equal(Convert.FromHexString("07009B3E"), c.Dict("DeviceProperties").Dict("Add").Dict("PciRoot(0x0)/Pci(0x2,0x0)").Get<PData>("AAPL,ig-platform-id").Value);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, c.Dict("DeviceProperties").Dict("Add").Dict("PciRoot(0x0)/Pci(0x1f,0x3)").Get<PData>("layout-id").Value);
        var acpi = c.Dict("ACPI").Array("Add").OfType<PDict>().Select(d => d.Str("Path")).ToList();
        Assert.Contains("SSDT-PLUG.aml", acpi); Assert.Contains("SSDT-AWAC.aml", acpi); Assert.Contains("SSDT-PMC.aml", acpi); Assert.Contains("SSDT-EC-USBX-DESKTOP.aml", acpi);
        Assert.Equal("Lilu.kext", c.Dict("Kernel").Array("Add").OfType<PDict>().First().Str("BundlePath"));
    }

    [Fact]
    public void AlderDesktop_CpuidSpoof_ProvideCurrentCpuInfo()
    {
        var c = Build("intel-alder-desktop-rx6600").Debug;
        Assert.Equal(Convert.FromHexString("55060A00000000000000000000000000"), ((PData)At(c, "Kernel.Emulate.Cpuid1Data")!).Value);
        Assert.Equal(Convert.FromHexString("FFFFFFFF000000000000000000000000"), ((PData)At(c, "Kernel.Emulate.Cpuid1Mask")!).Value);
        Assert.True(Bool(c, "Kernel.Quirks.ProvideCurrentCpuInfo"));
        Assert.True(Bool(c, "Booter.Quirks.ProtectUefiServices"));
        Assert.Contains("agdpmod=pikera", c.Dict("NVRAM").Dict("Add").Dict(ConfigBuilder.AppleVendorGuid).Str("boot-args"));
        Assert.Equal("MacPro7,1", c.Dict("PlatformInfo").Dict("Generic").Str("SystemProductName"));
    }

    [Fact]
    public void I2cLaptop_AddsXosiRename()
    {
        var c = Build("intel-comet-u-laptop").Debug;
        var patch = c.Dict("ACPI").Array("Patch").OfType<PDict>().Single();
        Assert.Equal(Encoding.ASCII.GetBytes("_OSI"), patch.Get<PData>("Find").Value);
        Assert.Equal(Encoding.ASCII.GetBytes("XOSI"), patch.Get<PData>("Replace").Value);
        var paths = c.Dict("Kernel").Array("Add").OfType<PDict>().Select(d => d.Str("BundlePath")!).ToList();
        Assert.True(paths.IndexOf("VoodooI2CHID.kext") > paths.IndexOf("VoodooI2C.kext"));
        Assert.Contains("itlwm.kext", paths); // Sequoia variant for Intel Wi-Fi
    }

    [Fact]
    public void MacosVariant_SelectsAirportItlwmOnOlderVersions()
    {
        var c = Build("intel-comet-u-laptop", macos: "sonoma").Debug;
        Assert.Contains("AirportItlwm.kext", c.Dict("Kernel").Array("Add").OfType<PDict>().Select(d => d.Str("BundlePath")));
    }

    [Fact]
    public void Kexts_DuplicateVoodooInput_IsDisabled()
    {
        var add = Build("intel-comet-u-laptop").Debug.Dict("Kernel").Array("Add").OfType<PDict>().ToList();
        var vi = add.Where(a => a.Str("BundlePath")!.EndsWith("VoodooInput.kext")).ToList();
        Assert.Equal(2, vi.Count);
        Assert.Single(vi, a => ((PBool)a["Enabled"]!).Value);
    }

    [Fact]
    public void Sections_Cleared_ReleaseToggle()
    {
        var o = Build("t495");
        var c = o.Debug;
        Assert.Equal(0, c.Dict("Kernel").Array("Block").Count);
        Assert.Equal(0, c.Dict("Misc").Array("Tools").Count);
        Assert.Null(c.Dict("DeviceProperties").Dict("Add")["PciRoot(0x0)/Pci(0x1b,0x0)"]);
        Assert.Equal(new[] { "OpenRuntime.efi", "HfsPlus.efi", "ResetNvramEntry.efi" }, c.Dict("UEFI").Array("Drivers").OfType<PDict>().Select(d => d.Str("Path")));
        Assert.Equal("", o.Release.Dict("NVRAM").Dict("Add").Dict(ConfigBuilder.AppleVendorGuid).Str("boot-args"));
        Assert.Equal(3, ((PInteger)PlistPath.Get(o.Release, "Misc.Debug.Target")!).Value);
        Assert.NotEmpty(o.DiffVsSample);
    }
}
