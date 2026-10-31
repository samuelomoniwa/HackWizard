using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HackWizard.Core.Logging;
using HackWizard.Core.Plist;
using Serilog;

namespace HackWizard.Core.Usb;

public enum UsbProtocol { Unknown, Usb2, Usb3 }

public sealed record UsbConnectorOption(int Value, string Label);

/// <summary>Apple UsbConnector values used in USB maps.</summary>
public static class UsbConnectors
{
    public static IReadOnlyList<UsbConnectorOption> All { get; } = new[]
    {
        new UsbConnectorOption(0, "USB 2 Type-A"),
        new UsbConnectorOption(3, "USB 3 Type-A"),
        new UsbConnectorOption(8, "Type-C (USB 2 only)"),
        new UsbConnectorOption(9, "Type-C (both orientations, same port)"),
        new UsbConnectorOption(10, "Type-C (other orientation uses another port)"),
        new UsbConnectorOption(255, "Internal"),
    };

    /// <summary>Best guess from what Windows reports (same idea as USBToolBox's guessing).</summary>
    public static int Guess(UsbProtocol protocol, bool userConnectable, bool typeC, int companionPort)
    {
        if (!userConnectable) return 255;
        var usb3Capable = protocol == UsbProtocol.Usb3 || companionPort > 0;
        if (typeC) return usb3Capable ? 9 : 8;
        return usb3Capable ? 3 : 0;
    }
}

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class UsbPort : Notifier
{
    /// <summary>Root-hub port number (= xHCI port number = "port" address in the macOS map).</summary>
    public int Number { get; set; }
    public UsbProtocol Protocol { get; set; }
    public bool UserConnectable { get; set; }
    public bool TypeC { get; set; }
    /// <summary>Port number of the USB 2/USB 3 partner of the same physical connector (0 = none).</summary>
    public int CompanionPort { get; set; }
    public string Name { get; set; } = "";

    private int _connector;
    public int Connector { get => _connector; set => Set(ref _connector, value); }
    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    private bool _seen;
    public bool Seen { get => _seen; set { if (Set(ref _seen, value)) Raise(nameof(LiveText)); } }
    private string _lastDevice = "";
    public string LastDevice { get => _lastDevice; set => Set(ref _lastDevice, value ?? ""); }
    private bool _connected;
    [JsonIgnore] public bool ConnectedNow { get => _connected; set { if (Set(ref _connected, value)) Raise(nameof(LiveText)); } }

    [JsonIgnore] public string ProtocolText => (Protocol switch { UsbProtocol.Usb3 => "USB 3", UsbProtocol.Usb2 => "USB 2", _ => "?" }) + (UserConnectable ? "" : " (internal)");
    [JsonIgnore] public string CompanionText => CompanionPort > 0 ? $"#{CompanionPort}" : "";
    [JsonIgnore] public string LiveText => ConnectedNow ? "● connected" : Seen ? "seen" : "";
}

public sealed class UsbController : Notifier
{
    public string InstanceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string AcpiPath { get; set; } = "";
    public bool IsXhci { get; set; }
    public string Note { get; set; } = "";
    public List<UsbPort> Ports { get; set; } = new();

    [JsonIgnore] public string PrimaryMatch => $"0x{DeviceId}{VendorId}".ToLowerInvariant();
    [JsonIgnore] public int SelectedCount => Ports.Count(p => p.Selected);
    [JsonIgnore] public string Header => $"{Name}   [{VendorId}:{DeviceId}]   {AcpiPath}".TrimEnd();
    [JsonIgnore] public string CountText =>
        !IsXhci ? "Not a USB 3 (XHCI) controller — macOS does not need a map for it; skipped."
        : Ports.Count == 0 ? $"Ports could not be read. {Note}"
        : $"{SelectedCount} of {Ports.Count} ports selected (macOS limit: {UsbMapState.MaxPorts} per controller)"
          + (SelectedCount > UsbMapState.MaxPorts ? "   ⚠ too many — untick some" : "");

    public event Action? Changed;
    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        foreach (var p in Ports)
            p.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(UsbPort.Selected) or nameof(UsbPort.Connector))
                {
                    Raise(nameof(SelectedCount)); Raise(nameof(CountText));
                    Changed?.Invoke();
                }
            };
    }
}

/// <summary>The user's USB map: detected controllers/ports, discoveries and selections. Saved between runs.</summary>
public sealed class UsbMapState
{
    public const int MaxPorts = 15;
    public int SchemaVersion { get; set; } = 1;
    public List<UsbController> Controllers { get; set; } = new();

    [JsonIgnore] public int SelectedCount => Controllers.Where(c => c.IsXhci).Sum(c => c.SelectedCount);
    [JsonIgnore] public bool OverLimit => Controllers.Any(c => c.IsXhci && c.SelectedCount > MaxPorts);
    [JsonIgnore] public IEnumerable<UsbController> Mapped => Controllers.Where(c => c.IsXhci && c.SelectedCount > 0);

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public static string DefaultPath => Path.Combine(AppPaths.Work, "usbmap.json");

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Opts)); }
        catch (Exception ex) { Log.Warning(ex, "Could not save USB map state"); }
    }

    public static UsbMapState? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UsbMapState>(File.ReadAllText(path), Opts) : null; }
        catch (Exception ex) { Log.Warning(ex, "Ignoring unreadable USB map state"); return null; }
    }

    /// <summary>HS01.. for USB 2 ports, SS01.. for USB 3 ports, PR01.. otherwise (numbered in port order).</summary>
    public static void AssignNames(UsbController c)
    {
        var counters = new Dictionary<string, int>();
        foreach (var p in c.Ports.OrderBy(p => p.Number))
        {
            var prefix = p.Protocol switch { UsbProtocol.Usb2 => "HS", UsbProtocol.Usb3 => "SS", _ => "PR" };
            counters[prefix] = counters.GetValueOrDefault(prefix) + 1;
            p.Name = $"{prefix}{counters[prefix]:00}";
        }
    }

    /// <summary>Replaces the topology with a fresh scan, keeping the user's choices for unchanged controllers.</summary>
    public void MergeTopology(IEnumerable<UsbController> fresh)
    {
        var old = Controllers.GroupBy(c => c.InstanceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<UsbController>();
        foreach (var f in fresh)
        {
            if (old.TryGetValue(f.InstanceId, out var o) && o.Ports.Select(p => p.Number).SequenceEqual(f.Ports.Select(p => p.Number)))
                foreach (var p in f.Ports)
                {
                    var op = o.Ports.First(x => x.Number == p.Number);
                    p.Selected = op.Selected; p.Seen = op.Seen; p.LastDevice = op.LastDevice; p.Connector = op.Connector;
                }
            result.Add(f);
        }
        Controllers = result;
    }

    /// <summary>Applies a live scan (port number → device name, or null when empty). Newly seen ports and their
    /// companions are selected when autoSelect is set. Returns true if any selection changed.</summary>
    public bool ApplySnapshot(UsbController c, IReadOnlyDictionary<int, string?> live, bool autoSelect)
    {
        var changed = false;
        foreach (var p in c.Ports)
        {
            live.TryGetValue(p.Number, out var device);
            p.ConnectedNow = device != null;
            if (device == null) continue;
            p.LastDevice = device;
            var newlySeen = !p.Seen;
            p.Seen = true;
            if (autoSelect && newlySeen)
            {
                if (!p.Selected) { p.Selected = true; changed = true; }
                var comp = c.Ports.FirstOrDefault(x => x.Number == p.CompanionPort);
                if (comp != null && p.UserConnectable && !comp.Selected) { comp.Selected = true; changed = true; }
            }
        }
        return changed;
    }

    public void AutoSelect()
    {
        foreach (var c in Controllers.Where(c => c.IsXhci))
            foreach (var p in c.Ports)
            {
                var comp = c.Ports.FirstOrDefault(x => x.Number == p.CompanionPort);
                p.Selected = p.Seen || (p.UserConnectable && comp is { Seen: true });
            }
    }

    public void ClearSelection()
    {
        foreach (var p in Controllers.SelectMany(c => c.Ports)) p.Selected = false;
    }
}

/// <summary>Generates UTBMap.kext (codeless kext read by USBToolBox.kext), matching each controller by PCI vendor/device id.</summary>
public static class UtbMapWriter
{
    public const string BundleId = "com.hackwizard.UTBMap";
    public const string UsbToolBoxId = "com.dhinakg.USBToolBox.kext";

    public static PDict BuildInfoPlist(UsbMapState map)
    {
        var personalities = new PDict();
        var used = new HashSet<string>();
        foreach (var c in map.Mapped)
        {
            var leaf = c.AcpiPath.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.TrimEnd('_') ?? "";
            var key = leaf.Length > 0 ? leaf : $"XHC-{c.DeviceId}";
            var unique = key; var n = 2;
            while (!used.Add(unique)) unique = $"{key}-{n++}";

            var ports = new PDict();
            foreach (var p in c.Ports.Where(p => p.Selected).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var d = new PDict().Set("UsbConnector", (long)p.Connector).Set("port", BitConverter.GetBytes(p.Number));
                if (p.LastDevice.Length > 0) d.Set("Comment", p.LastDevice);
                ports[p.Name] = d;
            }
            var maxPort = c.Ports.Where(p => p.Selected).Max(p => p.Number);
            personalities[unique] = new PDict()
                .Set("CFBundleIdentifier", UsbToolBoxId)
                .Set("IOClass", "USBToolBox")
                .Set("IOMatchCategory", "USBToolBox")
                .Set("IOProviderClass", "IOPCIDevice")
                .Set("IOPCIPrimaryMatch", c.PrimaryMatch)
                .Set("IOProviderMergeProperties", new PDict().Set("ports", ports).Set("port-count", BitConverter.GetBytes(maxPort)));
        }

        return new PDict()
            .Set("CFBundleDevelopmentRegion", "English")
            .Set("CFBundleGetInfoString", "USB map generated by HackWizard")
            .Set("CFBundleIdentifier", BundleId)
            .Set("CFBundleInfoDictionaryVersion", "6.0")
            .Set("CFBundleName", "UTBMap")
            .Set("CFBundlePackageType", "KEXT")
            .Set("CFBundleShortVersionString", "1.0")
            .Set("CFBundleSignature", "????")
            .Set("CFBundleVersion", "1.0")
            .Set("IOKitPersonalities", personalities)
            .Set("OSBundleLibraries", new PDict().Set(UsbToolBoxId, "1.0.0"))
            .Set("OSBundleRequired", "Root");
    }

    public static void Write(UsbMapState map, string kextDir)
    {
        if (Directory.Exists(kextDir)) Directory.Delete(kextDir, true);
        Directory.CreateDirectory(Path.Combine(kextDir, "Contents"));
        PlistWriter.WriteFile(BuildInfoPlist(map), Path.Combine(kextDir, "Contents", "Info.plist"));
    }
}
