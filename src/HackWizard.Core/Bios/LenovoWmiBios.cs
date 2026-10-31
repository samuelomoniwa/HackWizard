using System.Management;
using HackWizard.Core.Rules;
using Serilog;

namespace HackWizard.Core.Bios;

public sealed class BiosSettingChange
{
    public string SettingId { get; init; } = "";
    public string Title { get; init; } = "";
    public string WmiName { get; init; } = "";
    public string Current { get; init; } = "";
    public string Target { get; init; } = "";
    public IReadOnlyList<string> Allowed { get; init; } = Array.Empty<string>();
    public bool Selected { get; set; } = true;
    public bool NeedsChange => !string.Equals(Current, Target, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Optional, opt-in BIOS configuration for Lenovo ThinkPads via Lenovo's documented WMI interface
/// (root\wmi: Lenovo_BiosSetting, Lenovo_GetBiosSelections, Lenovo_SetBiosSetting, Lenovo_SaveBiosSettings).
/// Nothing is written unless Apply is called with explicitly selected, confirmed changes. Other vendors get manual instructions.</summary>
public sealed class LenovoWmiBios
{
    private const string Ns = @"root\wmi";

    public static bool IsAvailable()
    {
        try
        {
            using var s = new ManagementObjectSearcher(Ns, "SELECT CurrentSetting FROM Lenovo_BiosSetting");
            return s.Get().Count > 0;
        }
        catch { return false; }
    }

    public Dictionary<string, string> ReadAll()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var s = new ManagementObjectSearcher(Ns, "SELECT CurrentSetting FROM Lenovo_BiosSetting");
        foreach (ManagementObject o in s.Get())
        {
            var cur = o["CurrentSetting"]?.ToString();
            if (string.IsNullOrEmpty(cur)) continue;
            var semi = cur.IndexOf(';'); if (semi >= 0) cur = cur[..semi];
            var comma = cur.IndexOf(',');
            if (comma > 0) map[cur[..comma]] = cur[(comma + 1)..];
        }
        return map;
    }

    public IReadOnlyList<string> GetSelections(string name)
    {
        try
        {
            using var cls = new ManagementClass(Ns, "Lenovo_GetBiosSelections", null);
            foreach (ManagementObject inst in cls.GetInstances())
            {
                var inp = inst.GetMethodParameters("GetBiosSelections");
                inp["Item"] = name;
                var outp = inst.InvokeMethod("GetBiosSelections", inp, null);
                return (outp?["Selections"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        catch (Exception ex) { Log.Debug(ex, "GetBiosSelections({Name}) failed", name); }
        return Array.Empty<string>();
    }

    public bool SupervisorPasswordSet()
    {
        try
        {
            using var s = new ManagementObjectSearcher(Ns, "SELECT PasswordState FROM Lenovo_BiosPasswordSettings");
            foreach (ManagementObject o in s.Get()) return (Convert.ToInt32(o["PasswordState"]) & 0x2) != 0;
        }
        catch { }
        return false;
    }

    public List<BiosSettingChange> Plan(IEnumerable<KbBiosSetting> settings)
    {
        var current = ReadAll();
        var plan = new List<BiosSettingChange>();
        foreach (var s in settings.Where(x => x.LenovoWmi != null))
        {
            var name = s.LenovoWmi!.Names.FirstOrDefault(current.ContainsKey);
            if (name is null) continue;
            var allowed = GetSelections(name);
            var target = s.LenovoWmi.PickLargest
                ? allowed.Select(a => (a, n: LeadingNumber(a))).Where(x => x.n > 0).OrderByDescending(x => x.n).Select(x => x.a).FirstOrDefault()
                : s.LenovoWmi.Values.FirstOrDefault(v => allowed.Count == 0 || allowed.Contains(v, StringComparer.OrdinalIgnoreCase));
            if (target is null) continue;
            plan.Add(new BiosSettingChange { SettingId = s.Id, Title = s.Title, WmiName = name, Current = current[name], Target = target, Allowed = allowed });
        }
        return plan;
    }

    private static double LeadingNumber(string s)
    {
        var digits = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return 0;
        return s.Contains("GB", StringComparison.OrdinalIgnoreCase) ? n * 1024 : n;
    }

    public string Apply(IEnumerable<BiosSettingChange> changes, string? supervisorPassword)
    {
        var auth = string.IsNullOrEmpty(supervisorPassword) ? "" : $",{supervisorPassword},ascii,us";
        var log = new List<string>();
        using var setCls = new ManagementClass(Ns, "Lenovo_SetBiosSetting", null);
        var setInst = setCls.GetInstances().Cast<ManagementObject>().FirstOrDefault() ?? throw new InvalidOperationException("Lenovo_SetBiosSetting not available.");
        foreach (var c in changes.Where(x => x.Selected && x.NeedsChange))
        {
            var inp = setInst.GetMethodParameters("SetBiosSetting");
            inp["parameter"] = $"{c.WmiName},{c.Target}{auth};";
            var ret = setInst.InvokeMethod("SetBiosSetting", inp, null)?["return"]?.ToString() ?? "(no result)";
            Log.Information("Lenovo SetBiosSetting {Name}: {Old} -> {New} = {Ret}", c.WmiName, c.Current, c.Target, ret);
            log.Add($"{c.WmiName}: {c.Current} → {c.Target}: {ret}");
            if (!ret.Equals("Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Setting {c.WmiName} failed: {ret}. Nothing was saved. " +
                    (ret.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ? "A supervisor password is required or incorrect." : ""));
        }
        using var saveCls = new ManagementClass(Ns, "Lenovo_SaveBiosSettings", null);
        var saveInst = saveCls.GetInstances().Cast<ManagementObject>().FirstOrDefault() ?? throw new InvalidOperationException("Lenovo_SaveBiosSettings not available.");
        var sp = saveInst.GetMethodParameters("SaveBiosSettings");
        sp["parameter"] = string.IsNullOrEmpty(supervisorPassword) ? "" : $"{supervisorPassword},ascii,us;";
        var sret = saveInst.InvokeMethod("SaveBiosSettings", sp, null)?["return"]?.ToString() ?? "(no result)";
        log.Add($"SaveBiosSettings: {sret}");
        Log.Information("Lenovo SaveBiosSettings = {Ret}", sret);
        if (!sret.Equals("Success", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Saving BIOS settings failed: " + sret);
        return string.Join(Environment.NewLine, log) + Environment.NewLine + "Changes take effect after a restart.";
    }
}
