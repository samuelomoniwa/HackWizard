using System.Text.Json;
using System.Text.Json.Serialization;
using HackWizard.Core.Models;

namespace HackWizard.Core.Rules;

public enum CompatStatus { Supported, Partial, Unsupported, Info }

public sealed class KbManifest
{
    public int SchemaVersion { get; set; }
    public string KbVersion { get; set; } = "";
    public string UpdateUrl { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new();
    /// <summary>Base64 ECDSA P-256/SHA-256 signature over KnowledgeBaseLoader.CanonicalBytes.</summary>
    public string Signature { get; set; } = "";
}

public sealed class KbRuleFile
{
    public int SchemaVersion { get; set; }
    public string Category { get; set; } = "";
    public List<KbRule> Rules { get; set; } = new();
}

public sealed class KbRule
{
    public string Id { get; set; } = "";
    /// <summary>device | disk | cpu | system | always</summary>
    public string Scope { get; set; } = "device";
    public string Component { get; set; } = "";
    public string Title { get; set; } = "";
    public KbMatch Match { get; set; } = new();
    public CompatStatus Status { get; set; } = CompatStatus.Info;
    public bool Blocking { get; set; }
    /// <summary>Disk scope only: block only if no internal disk is usable.</summary>
    public bool BlockingIfNoAlternative { get; set; }
    /// <summary>Applies only when no non-fallback rule matched the device (device scope) or the CPU (cpu scope).</summary>
    public bool Fallback { get; set; }
    /// <summary>Lower runs first; later config patches / SMBIOS / recommendations override earlier ones.</summary>
    public int Priority { get; set; }
    /// <summary>Device rules sharing a group are exclusive per device: only the first match (by priority) applies.</summary>
    public string? Group { get; set; }
    public List<string> Kexts { get; set; } = new();
    public List<string> ExcludeKexts { get; set; } = new();
    public List<string> BootArgs { get; set; } = new();
    public List<string> Ssdts { get; set; } = new();
    public Dictionary<string, JsonElement> ConfigPatches { get; set; } = new();
    /// <summary>Path "{device}" is replaced with the matched device's OpenCore path.</summary>
    public Dictionary<string, Dictionary<string, JsonElement>> DeviceProperties { get; set; } = new();
    public List<int> AudioLayouts { get; set; } = new();
    /// <summary>Resolve layout-ids from AppleALC's own codec database at build time.</summary>
    public bool AudioAutoLayout { get; set; }
    /// <summary>SMBIOS model this rule proposes; the highest-priority matching rule wins.</summary>
    public string? Smbios { get; set; }
    /// <summary>Capabilities this rule provides when matched (e.g. "gpu", "gpu-dgpu", "net-install").</summary>
    public List<string> Provides { get; set; } = new();
    /// <summary>Capability → blocking message when no matched rule provides it.</summary>
    public Dictionary<string, string> Requires { get; set; } = new();
    /// <summary>Capability → non-blocking warning when no matched rule provides it.</summary>
    public Dictionary<string, string> Recommends { get; set; } = new();
    public KbMacosAdvice? Macos { get; set; }
    public List<string> Notes { get; set; } = new();
    public List<string> PostInstall { get; set; } = new();
}

public sealed class KbMatch
{
    // device
    public string? Category { get; set; }
    public List<string>? PciIds { get; set; }
    public List<string>? NameRegex { get; set; }
    public string? DevicePathRegex { get; set; }
    // cpu
    public string? CpuVendor { get; set; }
    public List<int>? CpuFamilies { get; set; }
    public List<int>? CpuModels { get; set; }
    public int? CpuModelMin { get; set; }
    public int? CpuModelMax { get; set; }
    public int? CpuSteppingMin { get; set; }
    public int? CpuSteppingMax { get; set; }
    public string? CpuNameRegex { get; set; }
    // system
    public string? ManufacturerRegex { get; set; }
    public List<string>? MachineTypes { get; set; }
    public string? ModelRegex { get; set; }
    public bool? IsLaptop { get; set; }
    public string? TrackpadInterface { get; set; }
    public string? AcpiHid { get; set; }
    public bool? HasAcpiHid { get; set; }
    public List<string>? AcpiHidsAbsent { get; set; }
    public string? DsdtHas { get; set; }
    public bool? IsUefi { get; set; }
    /// <summary>"X64" or "Arm64".</summary>
    public string? Architecture { get; set; }
    // disk
    public string? DiskBus { get; set; }
    public string? DiskModelRegex { get; set; }
    // capabilities provided by earlier (lower-priority-number) rules
    public List<string>? HasCapability { get; set; }
    public List<string>? LacksCapability { get; set; }

    [JsonIgnore]
    public bool HasCpuFields => CpuVendor != null || CpuFamilies != null || CpuModels != null || CpuModelMin != null || CpuModelMax != null
                                || CpuSteppingMin != null || CpuSteppingMax != null || CpuNameRegex != null;
}

public sealed class KbMacosAdvice
{
    public string? Recommended { get; set; }
    public Dictionary<string, string> Warn { get; set; } = new();
    public Dictionary<string, string> Block { get; set; } = new();
    /// <summary>Blocks every macOS whose Darwin major is greater than this (21 = Monterey is the last).</summary>
    public int? MaxDarwin { get; set; }
    public string? MaxReason { get; set; }
}

public sealed class KbCpuCodenames
{
    public int SchemaVersion { get; set; }
    public List<KbCodename> Codenames { get; set; } = new();
}

public sealed class KbCodename
{
    public string Vendor { get; set; } = "";
    public int Family { get; set; }
    public int ModelMin { get; set; }
    public int ModelMax { get; set; }
    public int SteppingMin { get; set; }
    public int SteppingMax { get; set; } = 15;
    public string Name { get; set; } = "";
    public string Microarchitecture { get; set; } = "";
}

public sealed class KbKextSources
{
    public int SchemaVersion { get; set; }
    public List<KbSource> Sources { get; set; } = new();
}

public sealed class KbSource
{
    public string Id { get; set; } = "";
    /// <summary>opencore | kext | driver | patches | utility | tool | data</summary>
    public string Kind { get; set; } = "kext";
    /// <summary>github-release | github-zipball | raw</summary>
    public string Type { get; set; } = "github-release";
    public string Repo { get; set; } = "";
    public string AssetPattern { get; set; } = "";
    public string? ExcludePattern { get; set; }
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public List<string> Provides { get; set; } = new();
    public bool Optional { get; set; }
    public string License { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class KbMacosVersions
{
    public int SchemaVersion { get; set; }
    public List<KbMacosVersion> Versions { get; set; } = new();
    /// <summary>SMBIOS model -> processor name of the real Mac (used for the optional 'Mac model CPU name').</summary>
    public Dictionary<string, string> SmbiosCpuNames { get; set; } = new();
}

public sealed class KbMacosVersion
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int DarwinMajor { get; set; }
    public string BoardId { get; set; } = "";
    public string Mlb { get; set; } = "00000000000000000";
    public string OsType { get; set; } = "default";
    /// <summary>SMBIOS models Apple supports on this version (used to gate versions).</summary>
    public List<string> SupportedSmbios { get; set; } = new();
    public Dictionary<string, List<string>> KextVariants { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class KbModels
{
    public int SchemaVersion { get; set; }
    public List<KbModelGuide> Models { get; set; } = new();
    public Dictionary<string, KbVendorKeys> VendorDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<KbBiosSetting> GenericSettings { get; set; } = new();
}

public sealed class KbVendorKeys
{
    public string SetupKey { get; set; } = "";
    public string BootMenuKey { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class KbModelGuide
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ManufacturerRegex { get; set; } = "";
    public List<string> MachineTypes { get; set; } = new();
    public string? FamilyRegex { get; set; }
    public string SetupKey { get; set; } = "";
    public string BootMenuKey { get; set; } = "";
    public string SetupNotes { get; set; } = "";
    public List<KbBiosSetting> Settings { get; set; } = new();
    /// <summary>Append the generic settings that apply (filtered by When) after the model-specific ones.</summary>
    public bool IncludeGeneric { get; set; }
}

public sealed class KbBiosSetting
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string MenuPath { get; set; } = "";
    public string Value { get; set; } = "";
    public string Why { get; set; } = "";
    public string IfMissing { get; set; } = "";
    public bool Required { get; set; }
    public KbLenovoWmi? LenovoWmi { get; set; }
    /// <summary>Only show this setting when the system matches (CPU vendor, laptop/desktop, capabilities…).</summary>
    public KbMatch? When { get; set; }
}

public sealed class KbLenovoWmi
{
    public List<string> Names { get; set; } = new();
    public List<string> Values { get; set; } = new();
    public bool PickLargest { get; set; }
}

/// <summary>The fully loaded knowledge base.</summary>
public sealed class KnowledgeBase
{
    public KbManifest Manifest { get; init; } = new();
    public List<KbRule> Rules { get; init; } = new();
    public KbCpuCodenames Cpu { get; init; } = new();
    public KbKextSources Sources { get; init; } = new();
    public KbMacosVersions Macos { get; init; } = new();
    public KbModels Models { get; init; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    public string LookupCodename(CpuInfo cpu) =>
        Cpu.Codenames.FirstOrDefault(c => c.Vendor.Equals(cpu.Vendor, StringComparison.OrdinalIgnoreCase) && c.Family == cpu.Family
                                          && cpu.Model >= c.ModelMin && cpu.Model <= c.ModelMax && cpu.Stepping >= c.SteppingMin && cpu.Stepping <= c.SteppingMax) is { } c
            ? $"{c.Name} ({c.Microarchitecture})" : "Unknown";

    public KbSource? SourceProviding(string bundleName) =>
        Sources.Sources.FirstOrDefault(s => s.Provides.Any(p => p.Equals(bundleName, StringComparison.OrdinalIgnoreCase)));

    public KbSource Source(string id) =>
        Sources.Sources.FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException($"Download source '{id}' is not in the knowledge base.");

    public KbMacosVersion Version(string id) =>
        Macos.Versions.FirstOrDefault(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"macOS version '{id}' is not in the knowledge base.");
}
