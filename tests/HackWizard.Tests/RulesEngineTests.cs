using HackWizard.Core.Models;
using HackWizard.Core.Rules;
using Xunit;

namespace HackWizard.Tests;

public class RulesEngineTests
{
    private static CompatibilityResult Eval(string profile, Action<HardwareReport>? tweak = null, bool uefi = true)
    {
        var hw = TestData.Profile(profile); tweak?.Invoke(hw);
        return TestData.Eval(hw, uefi);
    }

    [Fact]
    public void T495_KnownLimitations()
    {
        var r = Eval("t495");
        CompatStatus S(string c) => r.Items.First(i => i.Component == c).Status;
        Assert.Equal(CompatStatus.Partial, S("Graphics (AMD iGPU)"));
        Assert.Equal(CompatStatus.Partial, S("Sleep / Wake"));
        Assert.Equal(CompatStatus.Unsupported, S("Fingerprint / IR camera"));
        Assert.Contains("AirDrop", r.Items.First(i => i.Component == "Wi-Fi").Notes);
        Assert.Contains("WhateverGreen.kext", r.ExcludedKexts);
        Assert.DoesNotContain("NVMeFix.kext", r.EffectiveKexts);
        Assert.Equal(11, r.AudioLayoutId);
        Assert.Contains(r.MacosWarnings, w => w.Key == "tahoe");
        Assert.Contains(r.PostInstall, s => s.Contains("trimforce"));
    }

    [Fact]
    public void Intel_GetsIntelQuirks_Amd_GetsAmdVanilla()
    {
        var intel = Eval("intel-coffee-desktop");
        Assert.DoesNotContain(intel.ConfigPatches, p => p.Key == "$amdVanilla");
        Assert.Contains(intel.ConfigPatches, p => p.Key == "Kernel.Quirks.AppleXcpmCfgLock" && p.Value.GetBoolean());
        var amd = Eval("amd-ryzen-desktop-rx580");
        Assert.Contains(amd.ConfigPatches, p => p.Key == "$amdVanilla");
        Assert.Contains(amd.ConfigPatches, p => p.Key == "Kernel.Quirks.ProvideCurrentCpuInfo" && p.Value.GetBoolean());
    }

    [Fact]
    public void AlderLake_Desktop_CpuidSpoof_And_DgpuMakesMacPro()
    {
        var r = Eval("intel-alder-desktop-rx6600");
        Assert.Equal("MacPro7,1", r.Smbios);
        Assert.Contains(r.ConfigPatches, p => p.Key == "Kernel.Emulate.Cpuid1Data");
        Assert.Contains("agdpmod=pikera", r.BootArgs);
        Assert.Contains(r.Warnings, w => w.StartsWith("Network:"));
    }

    [Fact]
    public void Desktop_WithoutSupportedGpu_IsBlocked()
    {
        var r = Eval("intel-alder-desktop-rx6600", hw => hw.Devices.RemoveAll(d => d.VendorId == "1002"));
        Assert.True(r.IsBlocked);
        Assert.Contains(r.BlockingReasons, b => b.StartsWith("Graphics"));
    }

    [Fact]
    public void Desktop_WithDgpu_UsesHeadlessIgpu()
    {
        var r = Eval("intel-coffee-desktop", hw => hw.Devices.Add(new DeviceRecord { Category = DeviceCategory.Gpu, Name = "Radeon RX 580", VendorId = "1002", DeviceId = "67DF", OcDevicePath = "PciRoot(0x0)/Pci(0x1,0x0)/Pci(0x0,0x0)" }));
        Assert.Contains("gpu-dgpu", r.Capabilities);
        Assert.Equal("0300983E", r.DeviceProperties["PciRoot(0x0)/Pci(0x2,0x0)"]["AAPL,ig-platform-id"].GetProperty("data").GetString());
    }

    [Fact]
    public void Haswell_IsLimitedToMonterey()
    {
        var r = Eval("intel-haswell-laptop");
        Assert.Equal("monterey", r.RecommendedMacos);
        Assert.True(r.MacosBlocks.ContainsKey("ventura"));
    }

    [Fact]
    public void Nvidia_IsDisabled_NotBlocking()
    {
        var r = Eval("intel-coffee-desktop", hw => hw.Devices.Add(new DeviceRecord { Category = DeviceCategory.Gpu, Name = "RTX 3060", VendorId = "10DE", DeviceId = "2503", OcDevicePath = "PciRoot(0x0)/Pci(0x1,0x0)/Pci(0x0,0x0)" }));
        Assert.False(r.IsBlocked);
        Assert.True(r.DeviceProperties["PciRoot(0x0)/Pci(0x1,0x0)/Pci(0x0,0x0)"].ContainsKey("disable-gpu"));
        Assert.Equal("07009B3E", r.DeviceProperties["PciRoot(0x0)/Pci(0x2,0x0)"]["AAPL,ig-platform-id"].GetProperty("data").GetString());
    }

    [Fact]
    public void LegacyBios_Blocks() => Assert.True(Eval("t495", uefi: false).IsBlocked);

    [Fact]
    public void KnownBadNvme_WithSataAlternative_DoesNotBlock()
    {
        var r = Eval("intel-comet-u-laptop");
        Assert.False(r.IsBlocked);
        Assert.Contains(r.Items, i => i.RuleId == "disk.nvme.known-bad");
    }

    [Fact]
    public void UnsupportedWifi_IsReported()
    {
        var r = Eval("intel-alder-laptop");
        Assert.Contains(r.Items, i => i.RuleId == "wifi.fallback");
    }

    [Fact]
    public void BootArg_WithSameKey_IsReplaced()
    {
        var l = new List<string> { "-v", "alcid=1" };
        RulesEngine.AddBootArg(l, "alcid=11");
        Assert.Equal(new[] { "-v", "alcid=11" }, l);
    }

    [Theory]
    [InlineData("8086:9B41", "8086", "9B41", true)]
    [InlineData("8086:56*", "8086", "56A0", true)]
    [InlineData("8086:56*", "8086", "46A0", false)]
    [InlineData("10DE:*", "10DE", "2503", true)]
    [InlineData("10DE:*", "1002", "2503", false)]
    public void PciId_Wildcards(string pattern, string ven, string dev, bool expected) => Assert.Equal(expected, RulesEngine.MatchId(pattern, ven, dev));

    [Fact]
    public void Codenames()
    {
        var kb = TestData.Kb();
        Assert.StartsWith("Picasso", kb.LookupCodename(TestData.T495().Cpu));
        Assert.StartsWith("Coffee Lake", kb.LookupCodename(TestData.Profile("intel-coffee-desktop").Cpu));
        Assert.StartsWith("Kaby Lake R / Whiskey Lake / Comet Lake U", kb.LookupCodename(TestData.Profile("intel-comet-u-laptop").Cpu));
    }
}
