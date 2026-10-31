using System.Text;
using HackWizard.Core.Models;
using static HackWizard.Core.Acpi.Aml;

namespace HackWizard.Core.Acpi;

public sealed record GeneratedSsdt(string Name, string Dsl, byte[] Aml, string Explanation);

/// <summary>Generates the SSDTs requested by the rules (Dortania "Getting Started with ACPI" equivalents), using ACPI paths
/// read from this PC. Every table is emitted as human-readable DSL and compiled AML from the same parameters.
/// If a required path cannot be determined the SSDT is skipped with a warning instead of guessing.</summary>
public sealed class SsdtGenerator
{
    public const string OemId = "HACKWZ";
    private static readonly string[] WindowsOsi =
    {
        "Windows 2001", "Windows 2001 SP1", "Windows 2001.1", "Windows 2001 SP2", "Windows 2001.1 SP1", "Windows 2006",
        "Windows 2006 SP1", "Windows 2009", "Windows 2012", "Windows 2013", "Windows 2015", "Windows 2016", "Windows 2017",
        "Windows 2017.2", "Windows 2018", "Windows 2018.2", "Windows 2019", "Windows 2020", "Windows 2021", "Windows 2022"
    };

    public List<string> Warnings { get; } = new();

    public IReadOnlyList<GeneratedSsdt> Generate(IEnumerable<string> names, HardwareReport hw)
    {
        var list = new List<GeneratedSsdt>();
        foreach (var n in names)
        {
            GeneratedSsdt? s = n switch
            {
                "SSDT-EC-USBX-LAPTOP" or "SSDT-EC-USBX-DESKTOP" => EcUsbx(n, hw),
                "SSDT-PNLF" => Pnlf(),
                "SSDT-ALS0" => Als0(),
                "SSDT-XOSI" => Xosi(),
                "SSDT-PLUG" => Plug(hw),
                "SSDT-AWAC" => Awac(hw),
                "SSDT-PMC" => Pmc(hw),
                "SSDT-RHUB" => Rhub(hw),
                _ => throw new NotSupportedException($"SSDT '{n}' has no generator.")
            };
            if (s != null) list.Add(s);
        }
        return list;
    }

    private static byte[] DarwinOnlySta(byte onValue) =>
        Method("_STA", 0, false, If(Call("_OSI", Str("Darwin")), Return(Integer(onValue))), Else(Return(Integer(0))));

    private static string StaDsl(string value, string indent = "            ") =>
        $"{indent}Method (_STA, 0, NotSerialized)\n{indent}{{\n{indent}    If (_OSI (\"Darwin\"))\n{indent}    {{\n{indent}        Return ({value})\n{indent}    }}\n{indent}    Else\n{indent}    {{\n{indent}        Return (Zero)\n{indent}    }}\n{indent}}}\n";

    private static string Header(string tableId, uint rev = 0) => $"DefinitionBlock (\"\", \"SSDT\", 2, \"{OemId}\", \"{tableId}\", 0x{rev:X8})\n{{\n";

    /// <summary>LPC/eSPI bridge path: the PCI ISA bridge's ACPI name, else the EC's parent.</summary>
    internal static string? LpcPath(HardwareReport hw)
    {
        var lpc = hw.Devices.FirstOrDefault(d => d.Category == DeviceCategory.Lpc && d.AcpiPath.Length > 0);
        if (lpc != null) return DslPath(lpc.AcpiPath);
        var ec = hw.AcpiDevices.FirstOrDefault(a => a.Hid == "PNP0C09" && a.AcpiPath.Contains('.'));
        return ec is null ? null : DslPath(ec.AcpiPath[..ec.AcpiPath.LastIndexOf('.')]);
    }

    /// <summary>Returns (parent for fake EC, whether a fake EC is needed).</summary>
    internal (string parent, bool needFake) ResolveEc(HardwareReport hw)
    {
        var ec = hw.AcpiDevices.FirstOrDefault(a => a.Hid == "PNP0C09" && a.AcpiPath.Length > 0);
        if (ec is null)
        {
            var lpc = LpcPath(hw);
            if (lpc is null) Warnings.Add("No embedded controller or LPC bridge path found; the fake EC is placed under \\_SB.");
            return (lpc ?? "\\_SB", true);
        }
        var path = DslPath(ec.AcpiPath);
        var lastDot = path.LastIndexOf('.');
        var leaf = lastDot >= 0 ? path[(lastDot + 1)..] : path;
        var parent = lastDot > 0 ? path[..lastDot] : "\\_SB";
        return (parent, leaf != "EC");
    }

    internal GeneratedSsdt EcUsbx(string name, HardwareReport hw)
    {
        var (parent, needFake) = ResolveEc(hw);
        var body = new List<byte[]>();
        var dsl = new StringBuilder(Header("SsdtEC", 0x1000));
        if (needFake)
        {
            if (parent != "\\_SB") { body.Add(External(parent, ObjTypeDevice, 0)); dsl.Append($"    External ({parent}, DeviceObj)\n\n"); }
            body.Add(Scope(parent, Device("EC", Name("_HID", Str("ACID0001")), DarwinOnlySta(0x0F))));
            dsl.Append($"    Scope ({parent})\n    {{\n        Device (EC)\n        {{\n            Name (_HID, \"ACID0001\")  // _HID: Hardware ID\n")
               .Append(StaDsl("0x0F")).Append("        }\n    }\n\n");
        }
        body.Add(Scope("\\_SB", Device("USBX",
            Name("_ADR", Integer(0)),
            Method("_DSM", 4, false,
                If(LEqual(B(Arg2), Integer(0)), Return(Aml.Buffer(0x03))),
                Return(Package(Str("kUSBSleepPowerSupply"), Integer(0x13EC), Str("kUSBSleepPortCurrentLimit"), Integer(0x0834),
                               Str("kUSBWakePowerSupply"), Integer(0x13EC), Str("kUSBWakePortCurrentLimit"), Integer(0x0834)))),
            DarwinOnlySta(0x0F))));
        dsl.Append("    Scope (\\_SB)\n    {\n        Device (USBX)\n        {\n            Name (_ADR, Zero)  // _ADR: Address\n")
           .Append("            Method (_DSM, 4, NotSerialized)  // _DSM: Device-Specific Method\n            {\n")
           .Append("                If ((Arg2 == Zero))\n                {\n                    Return (Buffer (One) { 0x03 })\n                }\n\n")
           .Append("                Return (Package (0x08)\n                {\n                    \"kUSBSleepPowerSupply\", 0x13EC,\n                    \"kUSBSleepPortCurrentLimit\", 0x0834,\n                    \"kUSBWakePowerSupply\", 0x13EC,\n                    \"kUSBWakePortCurrentLimit\", 0x0834\n                })\n            }\n\n")
           .Append(StaDsl("0x0F")).Append("        }\n    }\n}\n");
        var why = needFake
            ? $"Creates a macOS-only fake 'EC' device under {parent} (the real EC keeps working for Windows/Linux) and USBX with USB power properties."
            : "A device named EC already exists, so only USBX (USB power properties) is added.";
        return new GeneratedSsdt(name, dsl.ToString(), Table("SSDT", 2, OemId, "SsdtEC", 0x1000, Cat(body.ToArray())), why);
    }

    internal static GeneratedSsdt Pnlf()
    {
        var dev = Device("PNLF", Name("_HID", EisaId("APP0002")), Name("_CID", Str("backlight")), Name("_UID", Integer(0)), DarwinOnlySta(0x0B));
        var dsl = Header("PNLF") + "    Scope (\\_SB)\n    {\n        Device (PNLF)\n        {\n" +
                  "            Name (_HID, EisaId (\"APP0002\"))  // _HID: Hardware ID\n            Name (_CID, \"backlight\")  // _CID: Compatible ID\n            Name (_UID, Zero)  // _UID: Unique ID\n" +
                  StaDsl("0x0B") + "        }\n    }\n}\n";
        return new GeneratedSsdt("SSDT-PNLF", dsl, Table("SSDT", 2, OemId, "PNLF", 0, Scope("\\_SB", dev)), "Backlight device so macOS shows a brightness slider (driven by WhateverGreen on Intel, NootedRed on AMD).");
    }

    internal static GeneratedSsdt Als0()
    {
        var dev = Device("ALS0", Name("_HID", Str("ACPI0008")), Name("_CID", Str("smc-als")), Name("_ALI", Integer(0x012C)),
            Name("_ALR", Package(Package(Integer(0x64), Integer(0x012C)))), DarwinOnlySta(0x0F));
        var dsl = Header("ALS0") + "    Scope (\\_SB)\n    {\n        Device (ALS0)\n        {\n" +
                  "            Name (_HID, \"ACPI0008\")  // _HID: Hardware ID\n            Name (_CID, \"smc-als\")  // _CID: Compatible ID\n            Name (_ALI, 0x012C)  // _ALI: Ambient Light Illuminance\n" +
                  "            Name (_ALR, Package (0x01)  // _ALR: Ambient Light Response\n            {\n                Package (0x02) { 0x64, 0x012C }\n            })\n" +
                  StaDsl("0x0F") + "        }\n    }\n}\n";
        return new GeneratedSsdt("SSDT-ALS0", dsl, Table("SSDT", 2, OemId, "ALS0", 0, Scope("\\_SB", dev)), "Fake ambient light sensor (macOS-only) so brightness is preserved; used with SMCLightSensor.");
    }

    internal static GeneratedSsdt Xosi()
    {
        var method = Method("XOSI", 1, false,
            Store(Package(WindowsOsi.Select(Str).ToArray()), B(Local0)),
            If(Call("_OSI", Str("Darwin")), Return(LNot(LEqual(B(OnesOp), Match(B(Local0), 1, B(Arg0), 0, Integer(0), Integer(0)))))),
            Else(Return(Call("_OSI", B(Arg0)))));
        var sb = new StringBuilder(Header("XOSI", 0x1000));
        sb.Append($"    Method (XOSI, 1, NotSerialized)\n    {{\n        Local0 = Package (0x{WindowsOsi.Length:X2})\n            {{\n");
        sb.Append(string.Join(",\n", WindowsOsi.Select(w => $"                \"{w}\""))).Append("\n            }\n");
        sb.Append("        If (_OSI (\"Darwin\"))\n        {\n            Return ((Ones != Match (Local0, MEQ, Arg0, MTR, Zero, Zero)))\n        }\n        Else\n        {\n            Return (_OSI (Arg0))\n        }\n    }\n}\n");
        return new GeneratedSsdt("SSDT-XOSI", sb.ToString(), Table("SSDT", 2, OemId, "XOSI", 0x1000, method),
            "Makes macOS appear as Windows to the firmware so I2C controllers are enabled. Paired with the _OSI→XOSI DSDT rename (added automatically).");
    }

    /// <summary>SSDT-PLUG: sets plugin-type=1 on the first CPU object (Intel Haswell–Comet Lake XCPM power management).</summary>
    internal GeneratedSsdt? Plug(HardwareReport hw)
    {
        var cpu = hw.AcpiDevices.FirstOrDefault(a => a.Hid is "CPU" or "ACPI0007" && a.AcpiPath.Length > 0);
        if (cpu is null) { Warnings.Add("SSDT-PLUG skipped: the first CPU's ACPI path could not be read. CPU power management will be basic."); return null; }
        var path = DslPath(cpu.AcpiPath);
        var isDevice = cpu.Hid == "ACPI0007";
        var dsm = Method("_DSM", 4, false,
            If(LNot(B(Arg2)), Return(Aml.Buffer(0x03))),
            Return(Package(Str("plugin-type"), Integer(1))));
        var body = Cat(External(path, isDevice ? ObjTypeDevice : ObjTypeProcessor, 0), Scope(path, If(Call("_OSI", Str("Darwin")), dsm)));
        var dsl = Header("CpuPlug", 0x3000) + $"    External ({path}, {(isDevice ? "DeviceObj" : "ProcessorObj")})\n\n    Scope ({path})\n    {{\n        If (_OSI (\"Darwin\"))\n        {{\n" +
                  "            Method (_DSM, 4, NotSerialized)  // _DSM: Device-Specific Method\n            {\n                If (!Arg2)\n                {\n                    Return (Buffer (One) { 0x03 })\n                }\n\n" +
                  "                Return (Package (0x02)\n                {\n                    \"plugin-type\", One\n                })\n            }\n        }\n    }\n}\n";
        return new GeneratedSsdt("SSDT-PLUG", dsl, Table("SSDT", 2, OemId, "CpuPlug", 0x3000, body), $"Enables native Intel CPU power management (XCPM) by setting plugin-type on {path}.");
    }

    /// <summary>SSDT-AWAC: if the DSDT has the STAS switch, flip it to the legacy RTC for macOS; otherwise add a fake RTC0.</summary>
    internal GeneratedSsdt? Awac(HardwareReport hw)
    {
        if (hw.DsdtSymbols.Contains("STAS"))
        {
            var body = Cat(External("STAS", ObjTypeInteger, 0), Scope("\\", If(Call("_OSI", Str("Darwin")), Store(Integer(1), Aml.NameString("STAS")))));
            var dsl = Header("AWAC") + "    External (STAS, IntObj)\n\n    Scope (\\)\n    {\n        If (_OSI (\"Darwin\"))\n        {\n            STAS = One\n        }\n    }\n}\n";
            return new GeneratedSsdt("SSDT-AWAC", dsl, Table("SSDT", 2, OemId, "AWAC", 0, body), "This board uses the AWAC clock; STAS=1 switches macOS back to the legacy RTC it needs.");
        }
        var lpc = LpcPath(hw);
        if (lpc is null) { Warnings.Add("SSDT-AWAC skipped: no STAS in DSDT and no LPC path for a fake RTC. macOS may not boot on this 300-series+ board."); return null; }
        var rtc = Device("RTC0", Name("_HID", EisaId("PNP0B00")),
            Name("_CRS", ResourceTemplate(ResIo16(0x70, 0x70, 0x01, 0x08), ResIrqNoFlags(8))), DarwinOnlySta(0x0F));
        var b = Cat(External(lpc, ObjTypeDevice, 0), Scope(lpc, rtc));
        var d = Header("RtcAwac") + $"    External ({lpc}, DeviceObj)\n\n    Scope ({lpc})\n    {{\n        Device (RTC0)\n        {{\n            Name (_HID, EisaId (\"PNP0B00\"))  // _HID: Hardware ID\n" +
                "            Name (_CRS, ResourceTemplate ()  // _CRS: Current Resource Settings\n            {\n                IO (Decode16, 0x0070, 0x0070, 0x01, 0x08, )\n                IRQNoFlags () {8}\n            })\n" +
                StaDsl("0x0F") + "        }\n    }\n}\n";
        return new GeneratedSsdt("SSDT-AWAC", d, Table("SSDT", 2, OemId, "RtcAwac", 0, b), $"This board has only the AWAC clock; a macOS-only legacy RTC0 is added under {lpc}.");
    }

    /// <summary>SSDT-PMC: NVRAM support on Intel 300-series desktop chipsets.</summary>
    internal GeneratedSsdt? Pmc(HardwareReport hw)
    {
        var lpc = LpcPath(hw);
        if (lpc is null) { Warnings.Add("SSDT-PMC skipped: LPC bridge path unknown. NVRAM may not persist."); return null; }
        var dev = Device("PMCR", Name("_HID", EisaId("APP9876")), DarwinOnlySta(0x0B),
            Name("_CRS", ResourceTemplate(ResMemory32Fixed(true, 0xFE000000, 0x00010000))));
        var b = Cat(External(lpc, ObjTypeDevice, 0), Scope(lpc, dev));
        var d = Header("PMCR", 0x1000) + $"    External ({lpc}, DeviceObj)\n\n    Scope ({lpc})\n    {{\n        Device (PMCR)\n        {{\n            Name (_HID, EisaId (\"APP9876\"))  // _HID: Hardware ID\n" +
                StaDsl("0x0B") + "\n            Name (_CRS, ResourceTemplate ()  // _CRS: Current Resource Settings\n            {\n                Memory32Fixed (ReadWrite, 0xFE000000, 0x00010000, )\n            })\n        }\n    }\n}\n";
        return new GeneratedSsdt("SSDT-PMC", d, Table("SSDT", 2, OemId, "PMCR", 0x1000, b), "Restores native NVRAM on Intel 300-series chipsets.");
    }

    /// <summary>SSDT-RHUB: hides the XHCI root hub from macOS so USB ports are rebuilt (Ice Lake and some Comet Lake boards).</summary>
    internal GeneratedSsdt? Rhub(HardwareReport hw)
    {
        var xhc = hw.Devices.FirstOrDefault(d => d.Category == DeviceCategory.Usb && d.VendorId == "8086" && d.AcpiPath.Length > 0);
        if (xhc is null) { Warnings.Add("SSDT-RHUB skipped: Intel XHCI controller ACPI path unknown."); return null; }
        var path = DslPath(xhc.AcpiPath) + ".RHUB";
        var body = Cat(External(path, ObjTypeDevice, 0), Scope(path,
            Method("_STA", 0, false, If(Call("_OSI", Str("Darwin")), Return(Integer(0))), Else(Return(Integer(0x0F))))));
        var d = Header("RhubOff") + $"    External ({path}, DeviceObj)\n\n    Scope ({path})\n    {{\n        Method (_STA, 0, NotSerialized)\n        {{\n" +
                "            If (_OSI (\"Darwin\"))\n            {\n                Return (Zero)\n            }\n            Else\n            {\n                Return (0x0F)\n            }\n        }\n    }\n}\n";
        if (!hw.DsdtSymbols.Contains("RHUB")) Warnings.Add($"SSDT-RHUB: 'RHUB' was not found in the DSDT; verify {path} exists (see Utilities/ACPI-Dump).");
        return new GeneratedSsdt("SSDT-RHUB", d, Table("SSDT", 2, OemId, "RhubOff", 0, body), $"Disables {path} in macOS only, so macOS enumerates the USB ports itself.");
    }
}
