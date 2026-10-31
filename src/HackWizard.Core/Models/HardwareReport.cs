using System.Text.Json.Serialization;

namespace HackWizard.Core.Models;

public enum DeviceCategory { Lpc, Gpu, Network, Wifi, Bluetooth, Storage, StorageController, Audio, AudioCodec, Input, Usb, Camera, CardReader, Biometric, Thunderbolt, Other }

public sealed class SystemInfo
{
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Lenovo machine type = first 4 characters of the product name (e.g. 20NJ).</summary>
    public string MachineType { get; set; } = "";
    public string Family { get; set; } = "";
    public string Version { get; set; } = "";
    public string BiosVendor { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public string BiosDate { get; set; } = "";
    public string Baseboard { get; set; } = "";
    public bool IsLaptop { get; set; }
    public long MemoryBytes { get; set; }
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
}

public sealed class CpuInfo
{
    public string Vendor { get; set; } = "";
    public string Name { get; set; } = "";
    public int Family { get; set; }
    public int Model { get; set; }
    public int Stepping { get; set; }
    public string Codename { get; set; } = "";
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
}

public sealed class DeviceRecord
{
    public DeviceCategory Category { get; set; }
    public string Name { get; set; } = "";
    public string PnpId { get; set; } = "";
    /// <summary>4-hex-digit vendor id, upper case (PCI VEN_, USB VID_ or HDA VEN_).</summary>
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string SubsystemId { get; set; } = "";
    public string Bus { get; set; } = "";
    /// <summary>ACPI path from DEVPKEY_Device_BiosDeviceName, e.g. \_SB.PCI0.GP17.ACP.</summary>
    public string AcpiPath { get; set; } = "";
    /// <summary>OpenCore-style device path converted from DEVPKEY_Device_LocationPaths.</summary>
    public string OcDevicePath { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public List<string> HardwareIds { get; set; } = new();
    public List<string> CompatibleIds { get; set; } = new();
    public string Service { get; set; } = "";
    [JsonIgnore] public string PciIdKey => VendorId.Length > 0 && DeviceId.Length > 0 ? $"{VendorId}:{DeviceId}" : "";
}

public sealed class DiskRecord
{
    public int Number { get; set; }
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    /// <summary>"NVMe", "SATA", "ATA", "USB", "RAID", "SD", "Unknown".</summary>
    public string BusType { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsSystem { get; set; }
    public bool IsBoot { get; set; }
    public bool IsRemovable { get; set; }
    public string PnpId { get; set; } = "";
}

public sealed class AcpiDeviceRecord
{
    /// <summary>ACPI HID (PNP0C09, ACPI000E…) or "CPU" for a Processor() object.</summary>
    public string Hid { get; set; } = "";
    public string AcpiPath { get; set; } = "";
    public string PnpId { get; set; } = "";
}

public sealed class HardwareReport
{
    public int SchemaVersion { get; set; } = 2;
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public string HostOs { get; set; } = "";
    /// <summary>Windows OS architecture: "X64" or "Arm64".</summary>
    public string HostArchitecture { get; set; } = "X64";
    public SystemInfo System { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public List<DeviceRecord> Devices { get; set; } = new();
    public List<DiskRecord> Disks { get; set; } = new();
    /// <summary>"PS2", "I2C" or "Unknown".</summary>
    public string TrackpadInterface { get; set; } = "Unknown";
    public string EthernetMac { get; set; } = "";
    /// <summary>Selected ACPI namespace objects (EC, LPC, AWAC, RTC, processor…) with their ACPI paths.</summary>
    public List<AcpiDeviceRecord> AcpiDevices { get; set; } = new();
    /// <summary>ACPI names found in the DSDT that influence SSDT generation (e.g. STAS, GPI0, RHUB).</summary>
    public List<string> DsdtSymbols { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    [JsonIgnore] public IEnumerable<DiskRecord> InternalDisks => Disks.Where(d => !d.IsRemovable && d.BusType != "USB");
}
