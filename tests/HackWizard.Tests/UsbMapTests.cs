using HackWizard.Core.Efi;
using HackWizard.Core.Plist;
using HackWizard.Core.Usb;
using Xunit;

namespace HackWizard.Tests;

public class UsbMapTests
{
    private static UsbMapState Sample(out UsbController c)
    {
        c = new UsbController
        {
            InstanceId = @"PCI\VEN_1022&DEV_15E0\0", Name = "AMD USB 3.10 eXtensible Host Controller", VendorId = "1022", DeviceId = "15E0",
            AcpiPath = @"\_SB_.PCI0.GP17.XHC0", IsXhci = true,
            Ports =
            {
                new UsbPort { Number = 1, Protocol = UsbProtocol.Usb2, UserConnectable = true, CompanionPort = 3 },
                new UsbPort { Number = 2, Protocol = UsbProtocol.Usb2, UserConnectable = false },
                new UsbPort { Number = 3, Protocol = UsbProtocol.Usb3, UserConnectable = true, CompanionPort = 1 },
            }
        };
        foreach (var p in c.Ports) p.Connector = UsbConnectors.Guess(p.Protocol, p.UserConnectable, p.TypeC, p.CompanionPort);
        UsbMapState.AssignNames(c);
        return new UsbMapState { Controllers = { c } };
    }

    [Theory]
    [InlineData(UsbProtocol.Usb2, true, false, 0, 0)]
    [InlineData(UsbProtocol.Usb2, true, false, 5, 3)]
    [InlineData(UsbProtocol.Usb3, true, false, 1, 3)]
    [InlineData(UsbProtocol.Usb3, true, true, 1, 9)]
    [InlineData(UsbProtocol.Usb2, true, true, 0, 8)]
    [InlineData(UsbProtocol.Usb2, false, false, 0, 255)]
    public void ConnectorGuess(UsbProtocol proto, bool user, bool typeC, int comp, int expected) =>
        Assert.Equal(expected, UsbConnectors.Guess(proto, user, typeC, comp));

    [Fact]
    public void Names_ByProtocol()
    {
        Sample(out var c);
        Assert.Equal(new[] { "HS01", "HS02", "SS01" }, c.Ports.Select(p => p.Name));
    }

    [Fact]
    public void Snapshot_SelectsSeenPortAndCompanion()
    {
        var s = Sample(out var c);
        Assert.True(s.ApplySnapshot(c, new Dictionary<int, string?> { [1] = "Mouse", [2] = null, [3] = null }, autoSelect: true));
        Assert.True(c.Ports[0].Selected); Assert.True(c.Ports[2].Selected); Assert.False(c.Ports[1].Selected);
        Assert.Equal("Mouse", c.Ports[0].LastDevice);
        Assert.False(s.ApplySnapshot(c, new Dictionary<int, string?> { [1] = "Mouse" }, autoSelect: true));
    }

    [Fact]
    public void Limit_Over15()
    {
        var s = new UsbMapState();
        var c = new UsbController { IsXhci = true, VendorId = "8086", DeviceId = "A36D" };
        for (var i = 1; i <= 16; i++) c.Ports.Add(new UsbPort { Number = i, Selected = true });
        s.Controllers.Add(c);
        Assert.True(s.OverLimit);
        c.Ports[0].Selected = false;
        Assert.False(s.OverLimit);
    }

    [Fact]
    public void Writer_ProducesUsbToolBoxMap_ReadableAsKext()
    {
        var s = Sample(out var c);
        c.Ports[0].Selected = true; c.Ports[2].Selected = true; c.Ports[0].LastDevice = "Mouse";
        var dir = Path.Combine(Directory.CreateTempSubdirectory("hw-utb").FullName, "UTBMap.kext");
        UtbMapWriter.Write(s, dir);
        var info = (PDict)PlistReader.ReadFile(Path.Combine(dir, "Contents", "Info.plist"));
        var pers = info.Dict("IOKitPersonalities").Dict("XHC0");
        Assert.Equal("USBToolBox", pers.Str("IOClass"));
        Assert.Equal("com.dhinakg.USBToolBox.kext", pers.Str("CFBundleIdentifier"));
        Assert.Equal("0x15e01022", pers.Str("IOPCIPrimaryMatch"));
        var merge = pers.Dict("IOProviderMergeProperties");
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, merge.Get<PData>("port-count").Value);
        var ports = merge.Dict("ports");
        Assert.Equal(new[] { "HS01", "SS01" }, ports.Keys);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, ports.Dict("HS01").Get<PData>("port").Value);
        Assert.Equal(3, ports.Dict("HS01").Get<PInteger>("UsbConnector").Value);
        Assert.Equal("1.0.0", info.Dict("OSBundleLibraries").Str("com.dhinakg.USBToolBox.kext"));

        var k = KextInfo.ReadWithPlugins(dir, "UTBMap.kext").Single();
        Assert.Equal("", k.ExecutablePath);
        Assert.Contains("com.dhinakg.USBToolBox.kext", k.Dependencies);
    }

    [Fact]
    public void Merge_KeepsUserChoices()
    {
        var s = Sample(out var c);
        c.Ports[1].Selected = true; c.Ports[1].Connector = 255; c.Ports[1].Seen = true;
        Sample(out var fresh);
        s.MergeTopology(new[] { fresh });
        Assert.True(s.Controllers[0].Ports[1].Selected);
        Assert.True(s.Controllers[0].Ports[1].Seen);
    }
}
