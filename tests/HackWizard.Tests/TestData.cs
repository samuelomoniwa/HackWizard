using System.Text.Json;
using HackWizard.Core.Efi;
using HackWizard.Core.Models;
using HackWizard.Core.Plist;
using HackWizard.Core.Rules;

namespace HackWizard.Tests;

internal static class TestData
{
    public static string Dir(params string[] parts) => Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray());

    private static KnowledgeBase? _kb;
    public static KnowledgeBase Kb() => _kb ??= KnowledgeBaseLoader.LoadFromDirectory(Dir("Rules"));

    public static IEnumerable<string> ProfileNames() =>
        Directory.GetFiles(Dir("Fixtures", "Profiles"), "*.hardware.json").Select(f => Path.GetFileName(f).Replace(".hardware.json", "")).OrderBy(x => x);

    public static HardwareReport Profile(string name) =>
        JsonSerializer.Deserialize<HardwareReport>(File.ReadAllText(Dir("Fixtures", "Profiles", name + ".hardware.json")), KnowledgeBase.JsonOptions)!;

    public static JsonElement Expected(string name) =>
        JsonDocument.Parse(File.ReadAllText(Dir("Fixtures", "Profiles", name + ".expected.json"))).RootElement;

    public static HardwareReport T495() => Profile("t495");

    public static CompatibilityResult Eval(HardwareReport hw, bool uefi = true) => new RulesEngine(Kb()).Evaluate(hw, new EvaluationContext { IsUefi = uefi });

    public static PDict Sample() => (PDict)PlistReader.ReadFile(Dir("Fixtures", "Sample.fixture.plist"));
    public static PNode Patches() => PlistReader.ReadFile(Dir("Fixtures", "patches.fixture.plist"));
    public static string AppleAlcFixture => Dir("Fixtures", "AppleALC");

    public static readonly SmbiosIdentity FixedIdentity = new("", "C02ZZ0ZZZZZZ", "C02000000000000AA", "11111111-2222-3333-4444-555555555555");

    /// <summary>Bundle id / dependency metadata of real kexts (as read from their Info.plist), so ordering can be tested without downloads.</summary>
    private static readonly Dictionary<string, (string Id, string[] Deps, string[] Plugins)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Lilu.kext"] = ("as.vit9696.Lilu", Array.Empty<string>(), Array.Empty<string>()),
        ["VirtualSMC.kext"] = ("as.vit9696.VirtualSMC", new[] { "as.vit9696.Lilu" }, Array.Empty<string>()),
        ["SMCBatteryManager.kext"] = ("ru.usrsse2.SMCBatteryManager", new[] { "as.vit9696.Lilu", "as.vit9696.VirtualSMC" }, Array.Empty<string>()),
        ["SMCLightSensor.kext"] = ("ru.joedm.SMCLightSensor", new[] { "as.vit9696.VirtualSMC" }, Array.Empty<string>()),
        ["VoodooPS2Controller.kext"] = ("as.acidanthera.voodoo.driver.PS2Controller", Array.Empty<string>(), new[] { "VoodooInput.kext", "VoodooPS2Keyboard.kext", "VoodooPS2Trackpad.kext" }),
        ["VoodooI2C.kext"] = ("com.alexandred.VoodooI2C", Array.Empty<string>(), new[] { "VoodooGPIO.kext", "VoodooInput.kext" }),
        ["VoodooI2CHID.kext"] = ("me.kishorprins.VoodooI2CHID", new[] { "com.alexandred.VoodooI2C" }, Array.Empty<string>()),
    };

    /// <summary>Synthesises KextInfo for a rules result (plugins included) the way EfiPipeline would after downloading.</summary>
    public static List<KextInfo> KextsFor(CompatibilityResult c, KbMacosVersion macos)
    {
        var list = new List<KextInfo>();
        foreach (var name in c.EffectiveKexts.SelectMany(k => k.StartsWith('@') ? macos.KextVariants[k[1..]] : new List<string> { k }).Select(k => k.Split('|')[0]).Distinct())
        {
            var (id, deps, plugins) = Known.TryGetValue(name, out var k) ? k : ("org.test." + Path.GetFileNameWithoutExtension(name), new[] { "as.vit9696.Lilu" }, Array.Empty<string>());
            list.Add(Make(name, id, deps));
            foreach (var p in plugins)
            {
                var pid = p == "VoodooInput.kext" ? "org.voodoo.driver.VoodooInput" : "org.test.plugin." + Path.GetFileNameWithoutExtension(p);
                list.Add(Make($"{name}/Contents/PlugIns/{p}", pid, new[] { id }, name));
            }
        }
        return list;
    }

    public static KextInfo Make(string path, string id, string[] deps, string? parent = null) => new()
    {
        BundlePath = path, BundleId = id, Version = "1.0.0", Dependencies = deps.Concat(new[] { "com.apple.kpi.libkern" }).ToList(),
        ExecutablePath = "Contents/MacOS/" + Path.GetFileNameWithoutExtension(path.Split('/').Last()), ParentBundlePath = parent
    };
}
