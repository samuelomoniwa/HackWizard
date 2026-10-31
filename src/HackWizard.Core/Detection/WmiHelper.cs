using System.Management;
using Serilog;

namespace HackWizard.Core.Detection;

/// <summary>Thin read-only WMI/CIM wrapper with defensive error handling.</summary>
public static class WmiHelper
{
    public static List<ManagementObject> Query(string scope, string wql)
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            s.Options.Timeout = TimeSpan.FromSeconds(30);
            return s.Get().Cast<ManagementObject>().ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WMI query failed: {Scope} {Wql}", scope, wql);
            return new List<ManagementObject>();
        }
    }

    public static List<ManagementObject> Cimv2(string wql) => Query(@"root\CIMV2", wql);

    public static string S(ManagementBaseObject o, string prop) { try { return o[prop]?.ToString()?.Trim() ?? ""; } catch { return ""; } }
    public static long L(ManagementBaseObject o, string prop) { try { return o[prop] is null ? 0 : Convert.ToInt64(o[prop]); } catch { return 0; } }
    public static bool B(ManagementBaseObject o, string prop) { try { return o[prop] is bool b && b; } catch { return false; } }
    public static string[] A(ManagementBaseObject o, string prop) { try { return o[prop] as string[] ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); } }

    /// <summary>Reads DEVPKEY_* properties via Win32_PnPEntity.GetDeviceProperties (Windows 8+).</summary>
    public static Dictionary<string, object?> DeviceProperties(ManagementObject pnpEntity, params string[] keys)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var inParams = pnpEntity.GetMethodParameters("GetDeviceProperties");
            inParams["devicePropertyKeys"] = keys;
            var outParams = pnpEntity.InvokeMethod("GetDeviceProperties", inParams, null);
            if (outParams?["deviceProperties"] is ManagementBaseObject[] props)
                foreach (var p in props)
                {
                    var key = p["KeyName"]?.ToString();
                    if (key is null) continue;
                    object? data = null;
                    try { data = p["Data"]; } catch { /* property not set */ }
                    result[key] = data;
                }
        }
        catch (Exception ex) { Log.Debug(ex, "GetDeviceProperties failed for {Id}", S(pnpEntity, "PNPDeviceID")); }
        return result;
    }

    public static ManagementObject? PnpEntityById(string instanceId)
    {
        var escaped = instanceId.Replace("\\", "\\\\").Replace("'", "\\'");
        return Cimv2($"SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID = '{escaped}'").FirstOrDefault();
    }
}
