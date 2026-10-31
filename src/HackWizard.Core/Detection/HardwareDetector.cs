using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using HackWizard.Core.Acpi;
using HackWizard.Core.Models;
using HackWizard.Core.Native;
using Serilog;
using static HackWizard.Core.Detection.WmiHelper;

namespace HackWizard.Core.Detection;

/// <summary>Read-only hardware inventory for any Windows PC using WMI/CIM, PnP device properties, CPUID and the DSDT.</summary>
public sealed class HardwareDetector
{
    private readonly Func<CpuInfo, string> _codenameLookup;
    public HardwareDetector(Func<CpuInfo, string> codenameLookup) => _codenameLookup = codenameLookup;

    /// <summary>ACPI HIDs whose presence/paths drive SSDT generation.</summary>
    internal static readonly string[] AcpiHidsOfInterest = { "PNP0C09", "PNP0A08", "PNP0A03", "ACPI0008", "ACPI000E", "PNP0B00", "PNP0C0D", "PNP0C0E" };

    /// <summary>DSDT names probed for SSDT decisions (e.g. STAS selects the AWAC variant).</summary>
    internal static readonly string[] DsdtProbes = { "STAS", "GPI0", "RHUB", "XOSI", "PMCR", "RTC_", "AWAC" };

    public Task<HardwareReport> DetectAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var r = new HardwareReport
        {
            HostOs = RuntimeInformation.OSDescription,
            HostArchitecture = NativeMethods.IsArm64Host() ? "Arm64" : "X64"
        };
        progress?.Report("Reading system and BIOS information…"); DetectSystem(r); ct.ThrowIfCancellationRequested();
        progress?.Report("Reading CPU (WMI + CPUID)…"); DetectCpu(r); ct.ThrowIfCancellationRequested();
        progress?.Report("Enumerating PCI/USB/HDA devices…"); DetectDevices(r, ct); ct.ThrowIfCancellationRequested();
        progress?.Report("Reading ACPI namespace objects and DSDT…"); DetectAcpi(r); ct.ThrowIfCancellationRequested();
        progress?.Report("Reading disks…"); DetectDisks(r); ct.ThrowIfCancellationRequested();
        progress?.Report("Reading network adapters (MAC for PlatformInfo ROM)…"); DetectEthernetMac(r);
        progress?.Report("Detection complete.");
        Log.Information("Hardware detected: {Mfr} {Model} / {Cpu} (fam 0x{Fam:X} model 0x{Mod:X} step {Step}) / {Devices} devices / {Disks} disks / laptop={Laptop}",
            r.System.Manufacturer, r.System.Model, r.Cpu.Name, r.Cpu.Family, r.Cpu.Model, r.Cpu.Stepping, r.Devices.Count, r.Disks.Count, r.System.IsLaptop);
        return r;
    }, ct);

    private static void DetectSystem(HardwareReport r)
    {
        var cs = Cimv2("SELECT Manufacturer, Model, SystemFamily, TotalPhysicalMemory, PCSystemType FROM Win32_ComputerSystem").FirstOrDefault();
        var csp = Cimv2("SELECT Name, Version, Vendor FROM Win32_ComputerSystemProduct").FirstOrDefault();
        var bios = Cimv2("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        var board = Cimv2("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var s = r.System;
        if (cs != null)
        {
            s.Manufacturer = S(cs, "Manufacturer"); s.Model = S(cs, "Model"); s.Family = S(cs, "SystemFamily");
            s.MemoryBytes = L(cs, "TotalPhysicalMemory");
            s.IsLaptop = L(cs, "PCSystemType") == 2;
        }
        if (csp != null)
        {
            s.Version = S(csp, "Version");
            var name = S(csp, "Name");
            if (s.Manufacturer.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) && name.Length >= 4) s.MachineType = name[..4].ToUpperInvariant();
            if (string.IsNullOrEmpty(s.Family)) s.Family = s.Version;
        }
        if (board != null) s.Baseboard = $"{S(board, "Manufacturer")} {S(board, "Product")}".Trim();
        if (bios != null)
        {
            s.BiosVendor = S(bios, "Manufacturer"); s.BiosVersion = S(bios, "SMBIOSBIOSVersion");
            var d = S(bios, "ReleaseDate");
            s.BiosDate = d.Length >= 8 ? $"{d[..4]}-{d.Substring(4, 2)}-{d.Substring(6, 2)}" : d;
        }
        var mem = Cimv2("SELECT Capacity FROM Win32_PhysicalMemory").Sum(m => L(m, "Capacity"));
        if (mem > 0) s.MemoryBytes = mem;
        // Chassis types per SMBIOS spec: portable/laptop/notebook/handheld/sub-notebook/tablet/convertible/detachable…
        var chassis = Cimv2("SELECT ChassisTypes FROM Win32_SystemEnclosure").FirstOrDefault();
        if (chassis?["ChassisTypes"] is ushort[] types && types.Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32)) s.IsLaptop = true;
        if (Cimv2("SELECT Name FROM Win32_Battery").Count > 0) s.IsLaptop = true;
        var vc = Cimv2("SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController")
            .OrderByDescending(v => L(v, "CurrentHorizontalResolution")).FirstOrDefault();
        if (vc != null) { s.DisplayWidth = (int)L(vc, "CurrentHorizontalResolution"); s.DisplayHeight = (int)L(vc, "CurrentVerticalResolution"); }
    }

    private void DetectCpu(HardwareReport r)
    {
        var procs = Cimv2("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        var sig = CpuIdReader.Read();
        r.Cpu.Vendor = sig.Vendor; r.Cpu.Family = sig.Family; r.Cpu.Model = sig.Model; r.Cpu.Stepping = sig.Stepping;
        r.Cpu.Name = procs.Select(p => S(p, "Name")).FirstOrDefault() ?? "";
        if (sig.Vendor == "Unknown") r.Cpu.Vendor = procs.Select(p => S(p, "Manufacturer")).FirstOrDefault() ?? "Unknown";
        r.Cpu.PhysicalCores = (int)procs.Sum(p => L(p, "NumberOfCores"));
        r.Cpu.LogicalProcessors = (int)procs.Sum(p => L(p, "NumberOfLogicalProcessors"));
        r.Cpu.Codename = _codenameLookup(r.Cpu);
    }

    private static readonly string[] PropKeys = { "DEVPKEY_Device_LocationPaths", "DEVPKEY_Device_BiosDeviceName", "DEVPKEY_Device_Parent" };

    private static void DetectDevices(HardwareReport r, CancellationToken ct)
    {
        var all = Cimv2("SELECT Name, PNPDeviceID, PNPClass, HardwareID, CompatibleID, Service, Present FROM Win32_PnPEntity");
        foreach (var e in all)
        {
            ct.ThrowIfCancellationRequested();
            if (e["Present"] is bool present && !present) continue;
            var id = S(e, "PNPDeviceID");
            var cls = S(e, "PNPClass");
            var name = S(e, "Name");
            var service = S(e, "Service");
            var hw = A(e, "HardwareID").ToList();
            var compat = A(e, "CompatibleID").ToList();
            var cat = Categorize(id, cls, name, service, compat);
            if (cat is null) continue;

            var (ven, dev, sub) = IdParser.Parse(id);
            var rec = new DeviceRecord
            {
                Category = cat.Value, Name = name, PnpId = id, VendorId = ven, DeviceId = dev, SubsystemId = sub,
                Bus = IdParser.BusOf(id), HardwareIds = hw, CompatibleIds = compat, Service = service
            };

            if (rec.Bus is "PCI" or "ACPI" || cat is DeviceCategory.AudioCodec)
            {
                var props = DeviceProperties(e, PropKeys);
                if (props.TryGetValue("DEVPKEY_Device_BiosDeviceName", out var acpi) && acpi is string a) rec.AcpiPath = a;
                if (props.TryGetValue("DEVPKEY_Device_LocationPaths", out var lp) && lp is string[] paths)
                    rec.OcDevicePath = DevicePathConverter.ToOpenCore(paths) ?? "";
                // HDA codec DeviceProperties belong to the parent PCI HDA controller.
                if (cat == DeviceCategory.AudioCodec && props.TryGetValue("DEVPKEY_Device_Parent", out var parent) && parent is string pid && PnpEntityById(pid) is { } p)
                {
                    var pp = DeviceProperties(p, PropKeys);
                    if (pp.TryGetValue("DEVPKEY_Device_LocationPaths", out var plp) && plp is string[] ppaths) rec.OcDevicePath = DevicePathConverter.ToOpenCore(ppaths) ?? "";
                    if (pp.TryGetValue("DEVPKEY_Device_BiosDeviceName", out var pa) && pa is string pas) rec.AcpiPath = pas;
                }
            }
            r.Devices.Add(rec);
        }

        var inputs = r.Devices.Where(d => d.Category == DeviceCategory.Input).ToList();
        var hasI2cHid = inputs.Any(d => d.CompatibleIds.Any(c => c.Contains("PNP0C50", StringComparison.OrdinalIgnoreCase)));
        var hasPs2Mouse = inputs.Any(d => d.Bus == "ACPI" && d.CompatibleIds.Any(c => c.Contains("PNP0F13", StringComparison.OrdinalIgnoreCase)));
        r.TrackpadInterface = hasI2cHid ? "I2C" : hasPs2Mouse ? "PS2" : "Unknown";
    }

    internal static DeviceCategory? Categorize(string id, string cls, string name, string service, IList<string> compat)
    {
        var bus = IdParser.BusOf(id);
        var n = name.ToLowerInvariant();
        if (id.StartsWith(@"HDAUDIO\FUNC_01", StringComparison.OrdinalIgnoreCase)) return DeviceCategory.AudioCodec;
        if (bus == "PCI" && compat.Any(c => c.StartsWith(@"PCI\CC_0601", StringComparison.OrdinalIgnoreCase))) return DeviceCategory.Lpc;
        if (n.Contains("thunderbolt")) return DeviceCategory.Thunderbolt;
        switch (cls)
        {
            case "Display" when bus == "PCI": return DeviceCategory.Gpu;
            case "Net" when bus == "PCI":
                return n.Contains("wireless") || n.Contains("wi-fi") || n.Contains("wifi") || n.Contains("802.11") || n.Contains("wlan")
                       || service.StartsWith("Netwtw", StringComparison.OrdinalIgnoreCase) || service.StartsWith("BCMPCIEDHD", StringComparison.OrdinalIgnoreCase)
                    ? DeviceCategory.Wifi : DeviceCategory.Network;
            case "Bluetooth" when bus == "USB": return DeviceCategory.Bluetooth;
            case "Camera" or "Image" when bus == "USB": return DeviceCategory.Camera;
            case "Biometric": return DeviceCategory.Biometric;
            case "SCSIAdapter" or "HDC" when bus == "PCI": return DeviceCategory.StorageController;
            case "SDHost" when bus == "PCI": return DeviceCategory.CardReader;
            case "USB" when bus == "PCI": return DeviceCategory.Usb;
            case "Keyboard" or "Mouse" when bus == "ACPI": return DeviceCategory.Input;
            case "HIDClass" when bus == "ACPI" || compat.Any(c => c.Contains("PNP0C50", StringComparison.OrdinalIgnoreCase)): return DeviceCategory.Input;
        }
        if (bus == "PCI" && (service.Equals("HDAudBus", StringComparison.OrdinalIgnoreCase) || cls == "MEDIA")) return DeviceCategory.Audio;
        if (bus == "PCI" && (service.StartsWith("Rts", StringComparison.OrdinalIgnoreCase) || n.Contains("card reader"))) return DeviceCategory.CardReader;
        return null;
    }

    private static void DetectAcpi(HardwareReport r)
    {
        foreach (var hid in AcpiHidsOfInterest)
            foreach (var e in Cimv2($@"SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'ACPI\\{hid}%'"))
                r.AcpiDevices.Add(new AcpiDeviceRecord { Hid = hid, AcpiPath = BiosName(e), PnpId = S(e, "PNPDeviceID") });

        // First processor object: Processor() objects enumerate as ACPI\GenuineIntel_-_… / ACPI\AuthenticAMD_-_…, Device objects as ACPI\ACPI0007.
        var cpu = Cimv2("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Processor'").FirstOrDefault();
        if (cpu != null)
        {
            var id = S(cpu, "PNPDeviceID");
            r.AcpiDevices.Add(new AcpiDeviceRecord { Hid = id.Contains("ACPI0007", StringComparison.OrdinalIgnoreCase) ? "ACPI0007" : "CPU", AcpiPath = BiosName(cpu), PnpId = id });
        }

        var dsdt = AcpiDumper.ReadDsdtFromRegistry();
        if (dsdt is null) { r.Warnings.Add("DSDT could not be read; SSDT variants fall back to safe defaults."); return; }
        var text = Encoding.ASCII.GetString(dsdt);
        foreach (var p in DsdtProbes) if (text.Contains(p, StringComparison.Ordinal)) r.DsdtSymbols.Add(p);
    }

    private static string BiosName(ManagementObject e)
    {
        var props = DeviceProperties(e, "DEVPKEY_Device_BiosDeviceName");
        return props.TryGetValue("DEVPKEY_Device_BiosDeviceName", out var v) && v is string s ? s : "";
    }

    private static void DetectDisks(HardwareReport r)
    {
        var disks = Query(@"root\Microsoft\Windows\Storage", "SELECT Number, FriendlyName, Model, SerialNumber, Size, BusType, IsSystem, IsBoot FROM MSFT_Disk");
        foreach (var d in disks)
        {
            var bus = IdParser.BusTypeName(L(d, "BusType"));
            var model = S(d, "Model"); if (model.Length == 0) model = S(d, "FriendlyName");
            r.Disks.Add(new DiskRecord
            {
                Number = (int)L(d, "Number"), Model = model, Serial = S(d, "SerialNumber"), BusType = bus,
                SizeBytes = L(d, "Size"), IsSystem = B(d, "IsSystem"), IsBoot = B(d, "IsBoot"), IsRemovable = bus is "USB" or "SD" or "MMC"
            });
        }
        if (disks.Count == 0)
        {
            foreach (var d in Cimv2("SELECT Index, Model, SerialNumber, Size, InterfaceType, MediaType, PNPDeviceID FROM Win32_DiskDrive"))
            {
                var pnp = S(d, "PNPDeviceID");
                var iface = S(d, "InterfaceType");
                var bus = iface == "USB" ? "USB" : pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase) ? "NVMe" : iface == "IDE" ? "SATA" : iface;
                r.Disks.Add(new DiskRecord { Number = (int)L(d, "Index"), Model = S(d, "Model"), Serial = S(d, "SerialNumber"), BusType = bus, SizeBytes = L(d, "Size"), IsRemovable = bus == "USB" || S(d, "MediaType").Contains("Removable"), PnpId = pnp });
            }
            r.Warnings.Add("MSFT_Disk unavailable; disk bus type derived from Win32_DiskDrive (less reliable).");
        }
    }

    private static void DetectEthernetMac(HardwareReport r)
    {
        foreach (var a in Query(@"root\StandardCimv2", "SELECT PermanentAddress, NdisPhysicalMedium, PnPDeviceID, Virtual FROM MSFT_NetAdapter"))
        {
            if (B(a, "Virtual")) continue;
            var pnp = S(a, "PnPDeviceID");
            if (!pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) continue;
            var mac = S(a, "PermanentAddress");
            var dev = r.Devices.FirstOrDefault(d => d.PnpId.Equals(pnp, StringComparison.OrdinalIgnoreCase));
            if (dev != null) dev.MacAddress = mac;
            if (L(a, "NdisPhysicalMedium") == 14 && mac.Length == 12 && string.IsNullOrEmpty(r.EthernetMac)) r.EthernetMac = mac.ToUpperInvariant();
        }
        // Fallback: any built-in adapter MAC (Wi-Fi is acceptable for ROM when there is no Ethernet).
        if (string.IsNullOrEmpty(r.EthernetMac))
            r.EthernetMac = r.Devices.Where(d => d.Category is DeviceCategory.Network or DeviceCategory.Wifi && d.MacAddress.Length == 12)
                                     .OrderBy(d => d.Category == DeviceCategory.Wifi).Select(d => d.MacAddress.ToUpperInvariant()).FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(r.EthernetMac)) r.Warnings.Add("No built-in network adapter MAC found; a random locally-administered ROM will be generated.");
    }
}
