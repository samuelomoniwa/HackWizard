using System.Text.Json;
using HackWizard.Core.Rules;
using Xunit;

namespace HackWizard.Tests;

/// <summary>Every hardware profile in Fixtures/Profiles is evaluated and compared with its .expected.json, which is produced by
/// tools/kb_tool.py (an independent Python implementation of the same rules). Adding support for a new PC = add a profile + rules.</summary>
public class ProfileTests
{
    public static IEnumerable<object[]> Profiles => TestData.ProfileNames().Select(n => new object[] { n });

    [Theory, MemberData(nameof(Profiles))]
    public void Profile_MatchesExpected(string name)
    {
        var r = TestData.Eval(TestData.Profile(name));
        var e = TestData.Expected(name);
        static List<string> Arr(JsonElement e, string p) => e.GetProperty(p).EnumerateArray().Select(x => x.GetString()!).ToList();

        Assert.Equal(e.GetProperty("blocked").GetBoolean(), r.IsBlocked);
        Assert.Equal(e.GetProperty("smbios").GetString(), r.Smbios);
        if (!r.IsBlocked) Assert.Equal(e.GetProperty("recommended").GetString(), r.RecommendedMacos);
        Assert.Equal(Arr(e, "blockedMacos"), r.MacosBlocks.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.Equal(Arr(e, "kexts"), r.EffectiveKexts.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.Equal(Arr(e, "ssdts"), r.Ssdts.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.Equal(Arr(e, "bootArgs"), r.BootArgs);
        Assert.Equal(Arr(e, "capabilities"), r.Capabilities.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.Equal(Arr(e, "matchedRules"), r.MatchedRuleIds.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());
        var props = e.GetProperty("deviceProperties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(x => x.GetString()!).ToList());
        Assert.Equal(props.Keys.OrderBy(x => x), r.DeviceProperties.Keys.OrderBy(x => x));
        foreach (var (path, keys) in props) Assert.Equal(keys, r.DeviceProperties[path].Keys.OrderBy(x => x, StringComparer.Ordinal).ToList());
        var lay = e.GetProperty("audioLayout");
        int? expectedLayout = lay.ValueKind == JsonValueKind.Null ? null : lay.GetInt32();
        Assert.Equal(expectedLayout, r.AudioLayoutId);
        Assert.Equal(e.GetProperty("audioCodec").GetString(), r.AudioCodecKey);
    }

    [Theory, MemberData(nameof(Profiles))]
    public void Evaluation_IsDeterministic(string name)
    {
        var a = TestData.Eval(TestData.Profile(name)); var b = TestData.Eval(TestData.Profile(name));
        Assert.Equal(a.MatchedRuleIds, b.MatchedRuleIds);
        Assert.Equal(a.Kexts, b.Kexts);
        Assert.Equal(a.ConfigPatches.Select(p => p.Key), b.ConfigPatches.Select(p => p.Key));
    }
}
