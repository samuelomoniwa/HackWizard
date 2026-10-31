using System.Text.Json;
using System.Text.RegularExpressions;
using HackWizard.Core.Models;

namespace HackWizard.Core.Rules;

public sealed record CompatItem(string Component, string Device, CompatStatus Status, string Notes, string RuleId, bool Blocking);

public sealed class EvaluationContext
{
    public bool IsUefi { get; init; } = true;
}

public sealed class CompatibilityResult
{
    public List<CompatItem> Items { get; } = new();
    public List<string> Kexts { get; } = new();
    public HashSet<string> ExcludedKexts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> BootArgs { get; } = new();
    public List<string> Ssdts { get; } = new();
    /// <summary>Config patches ordered by rule priority (later entries win).</summary>
    public List<KeyValuePair<string, JsonElement>> ConfigPatches { get; } = new();
    public Dictionary<string, Dictionary<string, JsonElement>> DeviceProperties { get; } = new(StringComparer.Ordinal);
    public int? AudioLayoutId { get; set; }
    public List<int> AudioLayoutCandidates { get; } = new();
    public string AudioDevicePath { get; set; } = "";
    /// <summary>Set when the layout must be resolved from AppleALC's database ("10EC:0256").</summary>
    public string AudioCodecKey { get; set; } = "";
    public string Smbios { get; set; } = "";
    public string SmbiosRuleId { get; set; } = "";
    public HashSet<string> Capabilities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string RecommendedMacos { get; set; } = "";
    public Dictionary<string, List<string>> MacosWarnings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> MacosBlocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> BlockingReasons { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> PostInstall { get; } = new();
    public List<string> MatchedRuleIds { get; } = new();
    public bool IsBlocked => BlockingReasons.Count > 0;
    public IEnumerable<string> EffectiveKexts => Kexts.Where(k => !ExcludedKexts.Contains(k.Split('|')[0]));
}

/// <summary>Deterministic, data-driven evaluator: same report + same KB ⇒ same result. Contains no hardware knowledge itself.</summary>
public sealed class RulesEngine
{
    private readonly KnowledgeBase _kb;
    public RulesEngine(KnowledgeBase kb) => _kb = kb;

    public CompatibilityResult Evaluate(HardwareReport hw, EvaluationContext ctx)
    {
        var res = new CompatibilityResult();
        var macosPriority = int.MinValue; var smbiosPriority = int.MinValue;
        var ordered = _kb.Rules.Select((r, i) => (r, i)).OrderBy(t => t.r.Priority).ThenBy(t => t.i).Select(t => t.r).ToList();
        var matchedDevices = new HashSet<DeviceRecord>(ReferenceEqualityComparer.Instance);
        var groups = new HashSet<(DeviceRecord, string)>();
        var cpuMatched = false;
        var unsupportedDisks = new HashSet<int>();
        var requires = new Dictionary<string, string>();
        var recommends = new Dictionary<string, string>();
        var maxDarwin = new List<(int Max, string Reason)>();

        void Apply(KbRule rule, string device, DeviceRecord? dev)
        {
            res.MatchedRuleIds.Add(rule.Id);
            var notes = string.Join(" ", rule.Notes);
            res.Items.Add(new CompatItem(rule.Component, device, rule.Status, notes, rule.Id, rule.Blocking));
            if (rule.Blocking) res.BlockingReasons.Add($"{rule.Component}: {device} — {notes}");
            foreach (var k in rule.Kexts) if (!res.Kexts.Contains(k, StringComparer.OrdinalIgnoreCase)) res.Kexts.Add(k);
            foreach (var k in rule.ExcludeKexts) res.ExcludedKexts.Add(k);
            foreach (var b in rule.BootArgs) AddBootArg(res.BootArgs, b);
            foreach (var s in rule.Ssdts) if (!res.Ssdts.Contains(s)) res.Ssdts.Add(s);
            foreach (var p in rule.ConfigPatches) res.ConfigPatches.Add(p);
            foreach (var (path, props) in rule.DeviceProperties)
            {
                var resolved = path == "{device}" ? dev?.OcDevicePath ?? "" : path;
                if (string.IsNullOrEmpty(resolved)) { res.Warnings.Add($"{rule.Id}: device path unknown, DeviceProperties skipped for {device}."); continue; }
                if (!res.DeviceProperties.TryGetValue(resolved, out var target)) res.DeviceProperties[resolved] = target = new(StringComparer.Ordinal);
                foreach (var kv in props) target[kv.Key] = kv.Value;
            }
            if (res.AudioLayoutId is null && string.IsNullOrEmpty(res.AudioCodecKey) && (rule.AudioLayouts.Count > 0 || rule.AudioAutoLayout))
            {
                res.AudioDevicePath = dev?.OcDevicePath ?? "";
                if (rule.AudioLayouts.Count > 0) { res.AudioLayoutId = rule.AudioLayouts[0]; res.AudioLayoutCandidates.AddRange(rule.AudioLayouts); }
                else if (dev != null) res.AudioCodecKey = dev.PciIdKey;
            }
            if (!string.IsNullOrEmpty(rule.Smbios) && rule.Priority >= smbiosPriority) { res.Smbios = rule.Smbios; res.SmbiosRuleId = rule.Id; smbiosPriority = rule.Priority; }
            foreach (var c in rule.Provides) res.Capabilities.Add(c);
            foreach (var (c, msg) in rule.Requires) requires.TryAdd(c, msg);
            foreach (var (c, msg) in rule.Recommends) recommends.TryAdd(c, msg);
            if (rule.Macos is { } m)
            {
                if (!string.IsNullOrEmpty(m.Recommended) && rule.Priority >= macosPriority) { res.RecommendedMacos = m.Recommended; macosPriority = rule.Priority; }
                foreach (var (v, why) in m.Warn) Add(res.MacosWarnings, v, why);
                foreach (var (v, why) in m.Block) Add(res.MacosBlocks, v, why);
                if (m.MaxDarwin is { } md) maxDarwin.Add((md, m.MaxReason ?? $"{rule.Component} is not supported after Darwin {md}."));
            }
            foreach (var pi in rule.PostInstall) if (!res.PostInstall.Contains(pi)) res.PostInstall.Add(pi);
        }

        foreach (var pass in new[] { false, true })
        {
            foreach (var rule in ordered.Where(r => r.Fallback == pass))
            {
                var m = rule.Match;
                switch (rule.Scope.ToLowerInvariant())
                {
                    case "device":
                        foreach (var d in hw.Devices)
                        {
                            if (pass && matchedDevices.Contains(d)) continue;
                            if (rule.Group != null && groups.Contains((d, rule.Group))) continue;
                            if (!MatchDevice(m, d) || !MatchSystem(m, hw, ctx, includeAcpi: false) || !MatchCaps(m, res)) continue;
                            matchedDevices.Add(d);
                            if (rule.Group != null) groups.Add((d, rule.Group));
                            Apply(rule, Describe(d), d);
                        }
                        break;
                    case "disk":
                        foreach (var disk in hw.InternalDisks)
                        {
                            if (!MatchDisk(m, disk)) continue;
                            if (rule.Status == CompatStatus.Unsupported) unsupportedDisks.Add(disk.Number);
                            Apply(rule, $"{disk.Model} ({disk.BusType}, {disk.SizeBytes / 1_000_000_000} GB)", null);
                        }
                        break;
                    case "cpu":
                        if (pass && cpuMatched) break;
                        if (MatchCpu(m, hw.Cpu) && MatchSystem(m, hw, ctx, includeAcpi: true) && MatchCaps(m, res))
                        {
                            if (!pass) cpuMatched = true;
                            Apply(rule, $"{hw.Cpu.Name} [{hw.Cpu.Codename}]", null);
                        }
                        break;
                    case "system":
                        if (MatchSystem(m, hw, ctx, includeAcpi: true) && MatchCaps(m, res))
                            Apply(rule, $"{hw.System.Manufacturer} {hw.System.Version} {hw.System.Model}".Trim(), null);
                        break;
                    case "always":
                        Apply(rule, "-", null);
                        break;
                    default:
                        throw new InvalidDataException($"Rule {rule.Id}: unknown scope '{rule.Scope}'.");
                }
            }
        }

        // Capability requirements (e.g. a GPU macOS can accelerate).
        foreach (var (cap, msg) in requires) if (!res.Capabilities.Contains(cap)) res.BlockingReasons.Add(msg);
        foreach (var (cap, msg) in recommends) if (!res.Capabilities.Contains(cap)) res.Warnings.Add(msg);

        // Storage.
        var internalDisks = hw.InternalDisks.ToList();
        if (internalDisks.Count == 0)
            res.BlockingReasons.Add("Storage: no internal disk detected to install macOS on.");
        else if (internalDisks.All(d => unsupportedDisks.Contains(d.Number)) &&
                 _kb.Rules.Any(r => r.Scope == "disk" && r.BlockingIfNoAlternative && res.MatchedRuleIds.Contains(r.Id)))
            res.BlockingReasons.Add("Storage: every internal disk is unsupported by macOS. Fit a supported SATA or NVMe SSD (see the disk notes) and run HackWizard again.");

        // SMBIOS and macOS version gating.
        if (string.IsNullOrEmpty(res.Smbios) && !res.IsBlocked)
            res.BlockingReasons.Add("No Mac model (SMBIOS) profile matches this CPU / form factor in the knowledge base.");
        foreach (var v in _kb.Macos.Versions)
        {
            if (res.Smbios.Length > 0 && v.SupportedSmbios.Count > 0 && !v.SupportedSmbios.Contains(res.Smbios, StringComparer.OrdinalIgnoreCase))
                Add(res.MacosBlocks, v.Id, $"Apple does not support the {res.Smbios} Mac model (chosen for this hardware) on {v.Name}.");
            foreach (var (max, reason) in maxDarwin) if (v.DarwinMajor > max) Add(res.MacosBlocks, v.Id, reason);
        }

        // Recommendation: explicit (highest priority) if allowed, else newest allowed version without warnings, else newest allowed.
        var allowed = _kb.Macos.Versions.Where(v => !res.MacosBlocks.ContainsKey(v.Id)).OrderByDescending(v => v.DarwinMajor).ToList();
        if (string.IsNullOrEmpty(res.RecommendedMacos) || res.MacosBlocks.ContainsKey(res.RecommendedMacos))
            res.RecommendedMacos = (allowed.FirstOrDefault(v => !res.MacosWarnings.ContainsKey(v.Id)) ?? allowed.FirstOrDefault())?.Id ?? "";
        if (string.IsNullOrEmpty(res.RecommendedMacos) && res.Smbios.Length > 0)
            res.BlockingReasons.Add("No macOS version in the knowledge base supports this hardware combination.");
        return res;
    }

    public static string Describe(DeviceRecord d) => string.IsNullOrEmpty(d.PciIdKey) ? d.Name : $"{d.Name} [{d.PciIdKey}]";

    /// <summary>Adds a boot-arg; an arg with "=" replaces an existing one with the same key.</summary>
    public static void AddBootArg(List<string> args, string arg)
    {
        var key = arg.Split('=')[0];
        args.RemoveAll(a => a.Split('=')[0] == key);
        args.Add(arg);
    }

    private static void Add(Dictionary<string, List<string>> d, string k, string v)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new();
        if (!l.Contains(v)) l.Add(v);
    }

    internal static bool MatchDevice(KbMatch m, DeviceRecord d)
    {
        if (m.Category != null && !m.Category.Equals(d.Category.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (m.PciIds is { Count: > 0 } && !m.PciIds.Any(p => MatchId(p, d.VendorId, d.DeviceId))) return false;
        if (m.NameRegex is { Count: > 0 } && !m.NameRegex.Any(rx => Regex.IsMatch(d.Name, rx, RegexOptions.IgnoreCase))) return false;
        if (m.DevicePathRegex != null && !Regex.IsMatch(d.OcDevicePath, m.DevicePathRegex)) return false;
        if (m.AcpiHid != null && !d.PnpId.Contains(m.AcpiHid, StringComparison.OrdinalIgnoreCase) && !d.CompatibleIds.Any(c => c.Contains(m.AcpiHid, StringComparison.OrdinalIgnoreCase))) return false;
        return m.Category != null || m.PciIds != null || m.NameRegex != null || m.AcpiHid != null;
    }

    /// <summary>"VVVV:DDDD", "VVVV:*" or a prefix wildcard "VVVV:56*".</summary>
    internal static bool MatchId(string pattern, string ven, string dev)
    {
        var parts = pattern.Split(':');
        if (parts.Length != 2 || !parts[0].Equals(ven, StringComparison.OrdinalIgnoreCase)) return false;
        var p = parts[1];
        if (p == "*") return true;
        if (p.EndsWith('*')) return dev.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase);
        return p.Equals(dev, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchDisk(KbMatch m, DiskRecord d)
    {
        if (m.DiskBus != null && !m.DiskBus.Equals(d.BusType, StringComparison.OrdinalIgnoreCase)) return false;
        if (m.DiskModelRegex != null && !Regex.IsMatch(d.Model, m.DiskModelRegex, RegexOptions.IgnoreCase)) return false;
        return true;
    }

    internal static bool MatchCpu(KbMatch m, CpuInfo c)
    {
        if (m.CpuVendor != null && !m.CpuVendor.Equals(c.Vendor, StringComparison.OrdinalIgnoreCase)) return false;
        if (m.CpuFamilies is { Count: > 0 } && !m.CpuFamilies.Contains(c.Family)) return false;
        if (m.CpuModels is { Count: > 0 } && !m.CpuModels.Contains(c.Model)) return false;
        if (m.CpuModelMin is { } min && c.Model < min) return false;
        if (m.CpuModelMax is { } max && c.Model > max) return false;
        if (m.CpuSteppingMin is { } smin && c.Stepping < smin) return false;
        if (m.CpuSteppingMax is { } smax && c.Stepping > smax) return false;
        if (m.CpuNameRegex != null && !Regex.IsMatch(c.Name, m.CpuNameRegex, RegexOptions.IgnoreCase)) return false;
        return true;
    }

    internal static bool MatchCaps(KbMatch m, CompatibilityResult res)
    {
        if (m.HasCapability is { Count: > 0 } && !m.HasCapability.All(res.Capabilities.Contains)) return false;
        if (m.LacksCapability is { Count: > 0 } && m.LacksCapability.Any(res.Capabilities.Contains)) return false;
        return true;
    }

    internal static bool MatchSystem(KbMatch m, HardwareReport hw, EvaluationContext ctx, bool includeAcpi)
    {
        var s = hw.System;
        if (m.ManufacturerRegex != null && !Regex.IsMatch(s.Manufacturer, m.ManufacturerRegex, RegexOptions.IgnoreCase)) return false;
        if (m.MachineTypes is { Count: > 0 } && !m.MachineTypes.Contains(s.MachineType, StringComparer.OrdinalIgnoreCase)) return false;
        if (m.ModelRegex != null && !Regex.IsMatch($"{s.Model} {s.Version} {s.Family}", m.ModelRegex, RegexOptions.IgnoreCase)) return false;
        if (m.IsLaptop is { } lap && s.IsLaptop != lap) return false;
        if (m.TrackpadInterface != null && !m.TrackpadInterface.Equals(hw.TrackpadInterface, StringComparison.OrdinalIgnoreCase)) return false;
        if (m.IsUefi is { } u && ctx.IsUefi != u) return false;
        if (m.Architecture != null && !m.Architecture.Equals(hw.HostArchitecture, StringComparison.OrdinalIgnoreCase)) return false;
        if (includeAcpi)
        {
            if (m.AcpiHid != null)
            {
                var has = hw.AcpiDevices.Any(a => a.Hid.Equals(m.AcpiHid, StringComparison.OrdinalIgnoreCase));
                if (has != (m.HasAcpiHid ?? true)) return false;
            }
            if (m.AcpiHidsAbsent is { Count: > 0 } && hw.AcpiDevices.Any(a => m.AcpiHidsAbsent.Contains(a.Hid, StringComparer.OrdinalIgnoreCase))) return false;
            if (m.DsdtHas != null && !hw.DsdtSymbols.Contains(m.DsdtHas)) return false;
        }
        if (m.HasCpuFields && !MatchCpu(m, hw.Cpu)) return false;
        return true;
    }
}
