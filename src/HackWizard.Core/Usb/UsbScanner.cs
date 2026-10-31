using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using HackWizard.Core.Detection;
using HackWizard.Core.Native;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace HackWizard.Core.Usb;

/// <summary>Reads USB controllers and root-hub ports through the Windows USB IOCTLs (the same data USBToolBox uses):
/// port numbers, USB 2/3 protocol, user-connectable/internal, Type-C and companion ports, plus live connections.</summary>
public static class UsbScanner
{
    private const string HostControllerGuid = "{3abf6f2d-71c4-462a-8a92-1e6861e6af27}"; // GUID_DEVINTERFACE_USB_HOST_CONTROLLER
    private const uint IOCTL_USB_GET_ROOT_HUB_NAME = 0x220408;
    private const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408;
    private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448;
    private const uint IOCTL_USB_GET_HUB_INFORMATION_EX = 0x220454;
    private const uint IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES = 0x220458;
    private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x22045C;

    private static readonly ConcurrentDictionary<string, string> HubPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> DeviceNames = new(StringComparer.OrdinalIgnoreCase);

    public static List<UsbController> ListControllers()
    {
        var list = new List<UsbController>();
        foreach (var e in WmiHelper.Cimv2("SELECT DeviceID, Name, PNPDeviceID, CompatibleID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI%'"))
        {
            var compat = WmiHelper.A(e, "CompatibleID");
            if (!compat.Any(c => c.StartsWith(@"PCI\CC_0C03", StringComparison.OrdinalIgnoreCase))) continue;
            var id = WmiHelper.S(e, "PNPDeviceID");
            var (ven, dev, _) = IdParser.Parse(id);
            var props = WmiHelper.DeviceProperties(e, "DEVPKEY_Device_BiosDeviceName");
            var c = new UsbController
            {
                InstanceId = id, Name = WmiHelper.S(e, "Name"), VendorId = ven, DeviceId = dev,
                AcpiPath = props.TryGetValue("DEVPKEY_Device_BiosDeviceName", out var a) && a is string s ? s : "",
                IsXhci = compat.Any(x => x.StartsWith(@"PCI\CC_0C0330", StringComparison.OrdinalIgnoreCase))
            };
            if (c.IsXhci)
            {
                try { c.Ports = ReadTopology(id); }
                catch (Exception ex) { c.Note = ex.Message; Log.Warning(ex, "USB topology read failed for {Id}", id); }
                UsbMapState.AssignNames(c);
            }
            list.Add(c);
        }
        Log.Information("USB: {Count} controllers, {Ports} XHCI ports", list.Count, list.Sum(c => c.Ports.Count));
        return list.OrderBy(c => c.AcpiPath).ThenBy(c => c.InstanceId).ToList();
    }

    private static List<UsbPort> ReadTopology(string instanceId)
    {
        var hubPath = RootHubPath(instanceId) ?? throw new IOException("Root hub not found (driver not loaded?).");
        using var hub = Open(hubPath);
        if (hub.IsInvalid) throw new IOException($"Cannot open root hub (Win32 error {Marshal.GetLastWin32Error()}).");
        var count = PortCount(hub);
        var hubLink = hubPath[4..];
        var ports = new List<UsbPort>();
        for (var i = 1; i <= count; i++)
        {
            var p = new UsbPort { Number = i, UserConnectable = true };
            var props = Ioctl(hub, IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES, BitConverter.GetBytes(i), 1024);
            if (props is { Length: >= 16 })
            {
                var flags = BitConverter.ToUInt32(props, 8);
                p.UserConnectable = (flags & 1) != 0;
                p.TypeC = (flags & 8) != 0;
                var companion = BitConverter.ToUInt16(props, 14);
                var link = props.Length > 16 ? Encoding.Unicode.GetString(props, 16, props.Length - 16).Split('\0')[0] : "";
                if (companion > 0 && (link.Length == 0 || hubLink.Contains(link, StringComparison.OrdinalIgnoreCase) || link.Contains(hubLink, StringComparison.OrdinalIgnoreCase)))
                    p.CompanionPort = companion;
            }
            var input = new byte[16];
            BitConverter.GetBytes(i).CopyTo(input, 0);
            BitConverter.GetBytes(16).CopyTo(input, 4);
            BitConverter.GetBytes(7).CopyTo(input, 8); // ask about USB 1.1, 2.0 and 3.x
            var v2 = Ioctl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2, input, 16);
            if (v2 is { Length: >= 12 })
            {
                var protocols = BitConverter.ToUInt32(v2, 8);
                p.Protocol = (protocols & 4) != 0 ? UsbProtocol.Usb3 : (protocols & 3) != 0 ? UsbProtocol.Usb2 : UsbProtocol.Unknown;
            }
            p.Connector = UsbConnectors.Guess(p.Protocol, p.UserConnectable, p.TypeC, p.CompanionPort);
            ports.Add(p);
        }
        return ports;
    }

    /// <summary>Live state: port number → device name (null when nothing is connected).</summary>
    public static Dictionary<int, string?> ReadLive(UsbController c)
    {
        var result = new Dictionary<int, string?>();
        var hubPath = RootHubPath(c.InstanceId);
        if (hubPath is null) return result;
        using var hub = Open(hubPath);
        if (hub.IsInvalid) { HubPaths.TryRemove(c.InstanceId, out _); return result; }
        foreach (var p in c.Ports)
        {
            var info = Ioctl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, BitConverter.GetBytes(p.Number), 512);
            if (info is not { Length: >= 35 } || BitConverter.ToUInt32(info, 31) != 1) { result[p.Number] = null; continue; } // 1 = DeviceConnected
            var vid = BitConverter.ToUInt16(info, 12).ToString("X4");
            var pid = BitConverter.ToUInt16(info, 14).ToString("X4");
            result[p.Number] = DeviceName(vid, pid);
        }
        return result;
    }

    private static string DeviceName(string vid, string pid) => DeviceNames.GetOrAdd($"{vid}:{pid}", _ =>
    {
        var e = WmiHelper.Cimv2($@"SELECT Name FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID_{vid}&PID_{pid}%'").FirstOrDefault();
        var name = e is null ? "" : WmiHelper.S(e, "Name");
        return name.Length > 0 ? $"{name} ({vid}:{pid})" : $"USB device {vid}:{pid}";
    });

    private static string? RootHubPath(string instanceId)
    {
        if (HubPaths.TryGetValue(instanceId, out var cached)) return cached;
        using var h = Open(@"\\?\" + instanceId.Replace('\\', '#') + "#" + HostControllerGuid);
        if (h.IsInvalid) return null;
        var o = Ioctl(h, IOCTL_USB_GET_ROOT_HUB_NAME, Array.Empty<byte>(), 1024);
        if (o is not { Length: > 4 }) return null;
        var name = Encoding.Unicode.GetString(o, 4, o.Length - 4).Split('\0')[0];
        if (name.Length == 0) return null;
        var path = @"\\.\" + name;
        HubPaths[instanceId] = path;
        return path;
    }

    private static int PortCount(SafeFileHandle hub)
    {
        var ex = Ioctl(hub, IOCTL_USB_GET_HUB_INFORMATION_EX, Array.Empty<byte>(), 512);
        if (ex is { Length: >= 6 })
        {
            var n = BitConverter.ToUInt16(ex, 4);
            if (n > 0) return n;
        }
        var ni = Ioctl(hub, IOCTL_USB_GET_NODE_INFORMATION, Array.Empty<byte>(), 512);
        if (ni is { Length: >= 7 }) return ni[6];
        throw new IOException("Could not read the root hub's port count.");
    }

    private static SafeFileHandle Open(string path) =>
        NativeMethods.CreateFileW(path, NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);

    private static byte[]? Ioctl(SafeFileHandle h, uint code, byte[] input, int outSize)
    {
        var size = Math.Max(input.Length, outSize);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var zero = new byte[size];
            Array.Copy(input, zero, input.Length);
            Marshal.Copy(zero, 0, buf, size);
            if (!NativeMethods.DeviceIoControl(h, code, buf, input.Length, buf, outSize, out var ret, IntPtr.Zero)) return null;
            var r = new byte[Math.Clamp(ret, 0, size)];
            Marshal.Copy(buf, r, 0, r.Length);
            return r;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
