using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HackWizard.Core.Acpi;
using HackWizard.Core.Detection;
using HackWizard.Core.Efi;
using HackWizard.Core.Models;
using HackWizard.Core.Plist;
using HackWizard.Core.Reports;
using HackWizard.Core.Rules;
using HackWizard.DiskWriter;
using HackWizard.Downloads;
using Xunit;

namespace HackWizard.Tests;

public class PlistTests
{
    [Fact]
    public void RoundTrip_IsDeterministicAndLossless()
    {
        var sample = TestData.Sample();
        var xml1 = PlistWriter.ToXml(sample);
        Assert.Equal(xml1, PlistWriter.ToXml(PlistReader.ReadString(xml1)));
        Assert.Empty(PlistDiff.Diff(sample, PlistReader.ReadString(xml1)));
    }

    [Fact]
    public void PreservesKeyOrder() =>
        Assert.Equal(new[] { "Z", "A", "M" }, ((PDict)PlistReader.ReadString(PlistWriter.ToXml(new PDict().Set("Z", 1L).Set("A", 2L).Set("M", 3L)))).Keys);

    [Fact]
    public void EscapesAndEncodesAllTypes()
    {
        var d = new PDict().Set("s", "a<b&c>").Set("t", true).Set("i", -5L).Set("d", new byte[] { 1, 2, 3 }).Set("e", "");
        var back = (PDict)PlistReader.ReadString(PlistWriter.ToXml(d));
        Assert.Equal("a<b&c>", back.Str("s"));
        Assert.True(back.Get<PBool>("t").Value);
        Assert.Equal(-5, back.Get<PInteger>("i").Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Get<PData>("d").Value);
        Assert.Equal("", back.Str("e"));
    }

    [Fact]
    public void PathSet_RejectsUnknownKeysAndTypeMismatch()
    {
        var s = TestData.Sample();
        Assert.Throws<PlistException>(() => PlistPath.Set(s, "Kernel.Quirks.DoesNotExist", new PBool(true)));
        Assert.Throws<PlistException>(() => PlistPath.Set(s, "Misc.Debug.Target", new PString("67")));
    }

    [Fact]
    public void EveryRuleConfigPath_ExistsInSample()
    {
        var s = TestData.Sample();
        foreach (var r in TestData.Kb().Rules)
            foreach (var (path, value) in r.ConfigPatches.Where(p => !p.Key.StartsWith('$')))
            {
                var existing = PlistPath.Get(s, path);
                Assert.True(existing != null, $"{r.Id}: {path} not in Sample.plist");
                PlistPath.Set((PDict)s.Clone(), path, ConfigBuilder.FromJson(value, existing));
            }
    }
}

public class AmlTests
{
    [Theory]
    [InlineData(0x3E, new byte[] { 0x3F })]
    [InlineData(0x3F, new byte[] { 0x41, 0x04 })]
    [InlineData(0x100, new byte[] { 0x42, 0x10 })]
    public void PkgLength_Encoding(int content, byte[] expected) => Assert.Equal(expected, Aml.PkgLength(content));

    [Fact]
    public void NameStrings()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("_OSI"), Aml.NameString("_OSI"));
        Assert.Equal(new byte[] { 0x5C }.Concat(Encoding.ASCII.GetBytes("_SB_")), Aml.NameString("\\_SB"));
        Assert.Equal(new byte[] { 0x5C, 0x2F, 0x03 }.Concat(Encoding.ASCII.GetBytes("_SB_PCI0LPCB")), Aml.NameString("\\_SB_.PCI0.LPCB"));
        Assert.Equal(new byte[] { 0x5C, 0x00 }, Aml.NameString("\\"));
        Assert.Equal("\\_PR.CPU0", Aml.DslPath("\\_PR_.CPU0"));
    }

    [Fact]
    public void EisaId_MatchesKnownEncoding()
    {
        Assert.Equal(new byte[] { 0x0C, 0x41, 0xD0, 0x0C, 0x09 }, Aml.EisaId("PNP0C09"));
        Assert.Equal(new byte[] { 0x0C, 0x06, 0x10, 0x00, 0x02 }, Aml.EisaId("APP0002"));
    }

    [Fact]
    public void ResourceDescriptors()
    {
        Assert.Equal(new byte[] { 0x47, 0x01, 0x70, 0x00, 0x70, 0x00, 0x01, 0x08 }, Aml.ResIo16(0x70, 0x70, 1, 8));
        Assert.Equal(new byte[] { 0x22, 0x00, 0x01 }, Aml.ResIrqNoFlags(8));
        Assert.Equal(new byte[] { 0x86, 0x09, 0x00, 0x01, 0, 0, 0, 0xFE, 0, 0, 1, 0 }, Aml.ResMemory32Fixed(true, 0xFE000000, 0x10000));
    }

    public static IEnumerable<object[]> AllProfiles => TestData.ProfileNames().Select(n => new object[] { n });

    [Theory, MemberData(nameof(AllProfiles))]
    public void GeneratedSsdts_ForEveryProfile_AreValidTables(string profile)
    {
        var hw = TestData.Profile(profile);
        var c = TestData.Eval(hw);
        foreach (var s in new SsdtGenerator().Generate(c.Ssdts, hw))
        {
            Assert.Equal("SSDT", Encoding.ASCII.GetString(s.Aml, 0, 4));
            Assert.Equal((uint)s.Aml.Length, BinaryPrimitives.ReadUInt32LittleEndian(s.Aml.AsSpan(4)));
            Assert.True(Aml.ChecksumValid(s.Aml), $"{profile}/{s.Name}");
            Assert.StartsWith("DefinitionBlock", s.Dsl);
            Assert.Equal(s.Dsl.Count(ch => ch == '{'), s.Dsl.Count(ch => ch == '}'));
        }
    }

    [Fact]
    public void EcSsdt_UsesDetectedPaths()
    {
        var s = new SsdtGenerator().Generate(new[] { "SSDT-EC-USBX-LAPTOP" }, TestData.T495())[0];
        Assert.Contains("Scope (\\_SB.PCI0.LPC0)", s.Dsl);
        Assert.Contains("Device (EC)", s.Dsl);
        var hw = TestData.T495(); hw.AcpiDevices[0].AcpiPath = "\\_SB.PCI0.LPC0.EC";
        Assert.DoesNotContain("Device (EC)", new SsdtGenerator().Generate(new[] { "SSDT-EC-USBX-LAPTOP" }, hw)[0].Dsl);
        var desk = new SsdtGenerator().Generate(new[] { "SSDT-EC-USBX-DESKTOP" }, TestData.Profile("intel-coffee-desktop"))[0];
        Assert.Contains("Scope (\\_SB.PCI0.LPCB)", desk.Dsl); // no EC: fake EC under the LPC bridge
    }

    [Fact]
    public void Plug_UsesProcessorOrDeviceObject()
    {
        var p = new SsdtGenerator().Generate(new[] { "SSDT-PLUG" }, TestData.Profile("intel-coffee-desktop"))[0];
        Assert.Contains("External (\\_PR.CPU0, ProcessorObj)", p.Dsl);
        Assert.True(ContainsBytes(p.Aml, Aml.External("\\_PR.CPU0", Aml.ObjTypeProcessor, 0)));
        var hw = TestData.Profile("intel-coffee-desktop");
        hw.AcpiDevices.RemoveAll(a => a.Hid == "CPU");
        var gen = new SsdtGenerator();
        Assert.Empty(gen.Generate(new[] { "SSDT-PLUG" }, hw));
        Assert.Single(gen.Warnings);
    }

    [Fact]
    public void Awac_Variants()
    {
        var hw = TestData.Profile("intel-coffee-desktop");
        Assert.Contains("STAS = One", new SsdtGenerator().Generate(new[] { "SSDT-AWAC" }, hw)[0].Dsl);
        hw.DsdtSymbols.Clear();
        var rtc = new SsdtGenerator().Generate(new[] { "SSDT-AWAC" }, hw)[0];
        Assert.Contains("Device (RTC0)", rtc.Dsl);
        Assert.Contains("Scope (\\_SB.PCI0.LPCB)", rtc.Dsl);
    }

    [Fact]
    public void Pmc_And_Rhub()
    {
        var hw = TestData.Profile("intel-coffee-desktop");
        var pmc = new SsdtGenerator().Generate(new[] { "SSDT-PMC" }, hw)[0];
        Assert.Contains("APP9876", pmc.Dsl);
        var rhub = new SsdtGenerator().Generate(new[] { "SSDT-RHUB" }, hw)[0];
        Assert.Contains("\\_SB.PCI0.XHC.RHUB", rhub.Dsl);
    }

    private static bool ContainsBytes(byte[] hay, byte[] needle)
    {
        for (var i = 0; i <= hay.Length - needle.Length; i++) if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
}

public class KextAndPatchTests
{
    [Fact]
    public void Order_DependenciesFirst()
    {
        var kb = TestData.Kb();
        var c = TestData.Eval(TestData.T495());
        var order = KextOrderer.Order(TestData.KextsFor(c, kb.Version("sequoia"))).Select(k => k.BundleId).ToList();
        Assert.Equal("as.vit9696.Lilu", order[0]);
        Assert.Equal("as.vit9696.VirtualSMC", order[1]);
        Assert.True(order.IndexOf("ru.joedm.SMCLightSensor") > order.IndexOf("as.vit9696.VirtualSMC"));
    }

    [Fact]
    public void Order_CycleThrows()
    {
        var x = new KextInfo { BundlePath = "X.kext", BundleId = "x", Dependencies = new() { "y" } };
        var y = new KextInfo { BundlePath = "Y.kext", BundleId = "y", Dependencies = new() { "x" } };
        Assert.Throws<InvalidOperationException>(() => KextOrderer.Order(new[] { x, y }));
    }

    [Fact]
    public void InfoPlist_ExecutablePath()
    {
        var info = new PDict().Set("CFBundleIdentifier", "a.b").Set("CFBundleExecutable", "Foo").Set("CFBundleVersion", "1.2.3");
        Assert.Equal("Contents/MacOS/Foo", KextInfo.FromInfoPlist(info, "Foo.kext", null, _ => true).ExecutablePath);
        Assert.Equal("", KextInfo.FromInfoPlist(info, "Foo.kext", null, _ => false).ExecutablePath);
    }

    [Theory] [InlineData(2)] [InlineData(6)] [InlineData(16)]
    public void AmdVanilla_SetsCoreCount(int cores)
    {
        var p = AmdVanillaPatcher.LoadAndPatch(TestData.Patches(), cores);
        Assert.All(p.OfType<PDict>().Where(d => d.Str("Comment")!.Contains("cpuid_cores_per_package")), d => Assert.Equal(cores, d.Get<PData>("Replace").Value[1]));
        Assert.Equal(3, AmdVanillaPatcher.CountCorePatches(p));
    }

    [Fact]
    public void AmdVanilla_UpstreamFormatGuard() =>
        Assert.Throws<PlistException>(() => AmdVanillaPatcher.LoadAndPatch(new PDict().Set("Kernel", new PDict().Set("Patch", new PArray())), 4));

    [Fact]
    public void AppleAlc_ResolvesLayoutsFromDatabase()
    {
        var r = AppleAlcLayouts.Find(TestData.AppleAlcFixture, "10EC:0236");
        Assert.NotNull(r);
        Assert.Equal("ALC236", r!.CodecName);
        Assert.Equal(3, r.Layouts[0]);
        Assert.Null(AppleAlcLayouts.Find(TestData.AppleAlcFixture, "10EC:9999"));
    }
}

public class DiskTests
{
    [Fact]
    public void Fat32_Layouts()
    {
        var l16 = Fat32Formatter.ComputeLayout(16L << 30, 512);
        Assert.Equal(16, l16.SectorsPerCluster);
        Assert.Equal(0, l16.DataStartSector % 2048);
        Assert.Equal(64, Fat32Formatter.ComputeLayout(64L << 30, 512).SectorsPerCluster);
        Assert.Throws<InvalidOperationException>(() => Fat32Formatter.ComputeLayout(32L << 20, 512));
    }

    [Fact]
    public void Fat32_Format_WritesValidStructures()
    {
        using var dev = new MemoryDevice(300L * 1024 * 1024);
        var l = Fat32Formatter.Format(dev, "opencore", 2048, 0x12345678);
        var d = dev.Data;
        Assert.Equal(0x55, d[510]); Assert.Equal(0xAA, d[511]);
        Assert.Equal("FAT32   ", Encoding.ASCII.GetString(d, 82, 8));
        Assert.Equal("OPENCORE   ", Encoding.ASCII.GetString(d, 71, 11));
        Assert.Equal(0x41615252u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(512)));
        Assert.Equal(d.AsSpan(0, 512).ToArray(), d.AsSpan(6 * 512, 512).ToArray());
        Assert.Equal(0x0FFFFFF8u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(l.ReservedSectors * 512)));
    }

    [Fact]
    public void Gpt_HeadersHaveValidCrc()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
        using var dev = new MemoryDevice(64L * 1024 * 1024);
        var (first, _) = GptWriter.Write(dev, "OPENCORE", Guid.Empty, Guid.Empty);
        Assert.Equal(2048, first);
        foreach (var lba in new[] { 1L, dev.Length / 512 - 1 })
        {
            var h = dev.Data.AsSpan((int)(lba * 512), 92).ToArray();
            Assert.Equal("EFI PART", Encoding.ASCII.GetString(h, 0, 8));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16));
            h[16] = h[17] = h[18] = h[19] = 0;
            Assert.Equal(crc, Crc32.Compute(h));
        }
    }

    [Fact]
    public void Diskpart_CapsPartition()
    {
        Assert.Equal(16384, UsbWriter.PartitionSizeMb(64_000_000_000, 16L << 30));
        Assert.Null(UsbWriter.PartitionSizeMb(15_000_000_000, 16L << 30));
        Assert.Contains("convert gpt", UsbWriter.DiskpartScript(3, null));
    }
}

public class DownloadTests
{
    [Fact]
    public void MacRecovery_PostBody()
    {
        var lines = MacRecoveryClient.BuildPostBody("Mac-7BA5B2D9E42DDD94", MacRecoveryClient.MlbZero, "default", n => new string('A', n)).Split('\n');
        Assert.Equal(new[] { "cid", "sn", "bid", "k", "fg", "os" }, lines.Select(l => l.Split('=')[0]));
        Assert.Matches("^[0-9A-F]{64}$", MacRecoveryClient.GenerateId(64));
    }

    [Fact]
    public void MacRecovery_ParseInfo()
    {
        var info = MacRecoveryClient.ParseInfo("AP: 041\nAU: http://x/BaseSystem.dmg\nAH: AA\nAT: t1\nCU: http://x/BaseSystem.chunklist\nCH: BB\nCT: t2\n");
        Assert.Equal("t1", info.ImageToken);
        Assert.Throws<DownloadException>(() => MacRecoveryClient.ParseInfo("AP: x\n"));
    }

    [Fact]
    public void Chunklist_VerifiesAndDetectsCorruption()
    {
        var dir = Directory.CreateTempSubdirectory("hw-cnk").FullName;
        var img = Path.Combine(dir, "BaseSystem.dmg"); var cnk = Path.Combine(dir, "BaseSystem.chunklist");
        var data = RandomNumberGenerator.GetBytes(10_000);
        File.WriteAllBytes(img, data);
        var sizes = new[] { 4096, 4096, 1808 };
        using (var bw = new BinaryWriter(File.Create(cnk)))
        {
            bw.Write(Encoding.ASCII.GetBytes("CNKL")); bw.Write(0x24u); bw.Write((byte)1); bw.Write((byte)1); bw.Write((byte)2); bw.Write((byte)0);
            bw.Write((ulong)sizes.Length); bw.Write(0x24UL); bw.Write(0x24UL + 0x24UL * (ulong)sizes.Length);
            var off = 0;
            foreach (var s in sizes) { bw.Write((uint)s); bw.Write(SHA256.HashData(data.AsSpan(off, s))); off += s; }
        }
        Assert.Equal(3, Chunklist.Verify(img, cnk));
        data[5000] ^= 0xFF; File.WriteAllBytes(img, data);
        Assert.Throws<InvalidDataException>(() => Chunklist.Verify(img, cnk));
    }

    [Fact]
    public void Github_ParseAndSelect()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"tag_name":"1.0.2","name":"x","published_at":"2026-01-01T00:00:00Z","zipball_url":"https://api.github.com/z","assets":[{"name":"Lilu-1.0.2-DEBUG.zip","browser_download_url":"u1","size":5},{"name":"Lilu-1.0.2-RELEASE.zip","browser_download_url":"u2","size":5,"digest":"sha256:abcdef"}]}""");
        var r = GitHubReleases.Parse(doc.RootElement);
        Assert.Equal("https://api.github.com/z", r.ZipballUrl);
        var a = DownloadManager.SelectAsset(r.Assets, "^Lilu-.*-RELEASE\\.zip$", null)!;
        Assert.Equal("u2", a.DownloadUrl);
        Assert.Equal("ABCDEF", a.Sha256Digest);
    }
}

public class MiscTests
{
    [Theory]
    [InlineData("PCIROOT(0)#PCI(0801)#PCI(0006)", "PciRoot(0x0)/Pci(0x8,0x1)/Pci(0x0,0x6)")]
    [InlineData("PCIROOT(0)#PCI(0200)", "PciRoot(0x0)/Pci(0x2,0x0)")]
    public void DevicePath_Conversion(string win, string oc) => Assert.Equal(oc, DevicePathConverter.ToOpenCore(new[] { "ACPI(_SB_)#ACPI(PCI0)", win }));

    [Theory]
    [InlineData("AuthenticAMD", 0x00810F81, 0x17, 0x18, 1)]   // Ryzen 3 3300U (Picasso)
    [InlineData("GenuineIntel", 0x000906ED, 6, 0x9E, 13)]     // i7-9700K (Coffee Lake)
    [InlineData("GenuineIntel", 0x00090672, 6, 0x97, 2)]      // i5-12600K (Alder Lake)
    [InlineData("AuthenticAMD", 0x00A20F10, 0x19, 0x21, 0)]   // Ryzen 5 5600X (Vermeer)
    public void CpuId_Decode(string vendor, int eax, int fam, int model, int step)
    {
        var s = CpuIdReader.Decode(vendor, eax);
        Assert.Equal((fam, model, step), (s.Family, s.Model, s.Stepping));
    }

    [Fact]
    public void Categorize_CommonDevices()
    {
        Assert.Equal(DeviceCategory.Lpc, HardwareDetector.Categorize(@"PCI\VEN_8086&DEV_A305", "System", "LPC Controller", "", new[] { @"PCI\CC_0601" }));
        Assert.Equal(DeviceCategory.Wifi, HardwareDetector.Categorize(@"PCI\VEN_8086&DEV_2723", "Net", "Intel(R) Wi-Fi 6 AX200 160MHz", "Netwtw10", Array.Empty<string>()));
        Assert.Equal(DeviceCategory.AudioCodec, HardwareDetector.Categorize(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0236", "MEDIA", "Realtek Audio", "", Array.Empty<string>()));
        Assert.Equal(DeviceCategory.Gpu, HardwareDetector.Categorize(@"PCI\VEN_10DE&DEV_2503", "Display", "NVIDIA GeForce RTX 3060", "", Array.Empty<string>()));
    }

    [Fact]
    public void KnowledgeBase_Integrity()
    {
        var kb = TestData.Kb();
        Assert.NotEmpty(kb.Manifest.Files);
        foreach (var r in kb.Rules)
            foreach (var k in r.Kexts.Where(x => !x.StartsWith('@')))
                Assert.True(kb.SourceProviding(k) != null, $"{r.Id}: no source for {k}");
        foreach (var v in kb.Macos.Versions)
            foreach (var k in v.KextVariants.Values.SelectMany(x => x))
                Assert.True(kb.SourceProviding(k) != null, $"{v.Id}: no source for {k}");
        foreach (var s in kb.Rules.Where(r => r.Smbios != null).Select(r => r.Smbios!))
            Assert.Contains(kb.Macos.Versions, v => v.SupportedSmbios.Contains(s));
        var tmp = Directory.CreateTempSubdirectory("hw-kb").FullName;
        KnowledgeBaseLoader.ExtractEmbedded(tmp);
        Assert.Equal(kb.Rules.Count, KnowledgeBaseLoader.LoadFromDirectory(tmp).Rules.Count);
    }

    [Fact]
    public void BiosGuide_ModelVsGeneric()
    {
        var kb = TestData.Kb();
        var t495 = BiosGuideGenerator.Build(kb, TestData.T495());
        Assert.True(t495.IsModelSpecific);
        Assert.Equal("F1", t495.SetupKey);
        Assert.Contains(t495.Settings, s => s.Id == "uma");

        var rembrandt = TestData.Profile("amd-rembrandt-laptop"); // ThinkPad T14 Gen 3 → generic ThinkPad profile + generic settings
        var g = BiosGuideGenerator.Build(kb, rembrandt, TestData.Eval(rembrandt));
        Assert.False(g.IsModelSpecific);
        Assert.Equal("F1", g.SetupKey);
        Assert.Equal("save", g.Settings.Last().Id);

        var amdDesk = TestData.Profile("amd-ryzen-desktop-rx580");
        var d = BiosGuideGenerator.Build(kb, amdDesk, TestData.Eval(amdDesk));
        Assert.Equal("Del (desktops) / Del or F2 (laptops)", d.SetupKey);
        Assert.Contains(d.Settings, s => s.Id == "above4g");
        Assert.DoesNotContain(d.Settings, s => s.Id is "cfglock" or "uma");   // Intel-only / APU-only settings filtered out

        var intelLaptop = TestData.Profile("intel-comet-u-laptop");
        var il = BiosGuideGenerator.Build(kb, intelLaptop, TestData.Eval(intelLaptop));
        Assert.Contains(il.Settings, s => s.Id == "dvmt");
        Assert.DoesNotContain(il.Settings, s => s.Id == "above4g");
    }

    [Fact]
    public void Pdf_IsWellFormed()
    {
        var s = Encoding.Latin1.GetString(SimplePdf.Create("Test", new[] { (true, "Heading"), (false, "Body (with parens) → arrow") }));
        Assert.StartsWith("%PDF-1.4", s);
        Assert.EndsWith("%%EOF\n", s);
        Assert.Contains("\\(with parens\\)", s);
    }
}
