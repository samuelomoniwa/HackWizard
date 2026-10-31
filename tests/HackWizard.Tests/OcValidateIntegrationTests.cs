using HackWizard.Core.Efi;
using HackWizard.Core.Plist;
using Xunit;

namespace HackWizard.Tests;

/// <summary>Runs OpenCore's real ocvalidate.exe on configs built from the real Sample.plist and AMD_Vanilla patches, for every
/// buildable profile. build.ps1 -IntegrationTests sets HACKWIZARD_OPENCORE_DIR and HACKWIZARD_AMD_PATCHES automatically.</summary>
public class OcValidateIntegrationTests
{
    public static IEnumerable<object[]> Buildable => ConfigBuilderTests.GoldenProfiles;

    [SkippableTheory, MemberData(nameof(Buildable))]
    public void Config_PassesOcValidate(string profile)
    {
        var oc = Environment.GetEnvironmentVariable("HACKWIZARD_OPENCORE_DIR");
        var patches = Environment.GetEnvironmentVariable("HACKWIZARD_AMD_PATCHES");
        Skip.If(string.IsNullOrEmpty(oc) || string.IsNullOrEmpty(patches), "HACKWIZARD_OPENCORE_DIR / HACKWIZARD_AMD_PATCHES not set.");
        var sample = (PDict)PlistReader.ReadFile(Path.Combine(oc!, "Docs", "Sample.plist"));
        var o = ConfigBuilderTests.Build(profile, sample, PlistReader.ReadFile(patches!));
        var dir = Directory.CreateTempSubdirectory("hw-ocv").FullName;
        foreach (var (name, cfg) in new[] { ("config.plist", o.Debug), ("config-release.plist", o.Release) })
        {
            var path = Path.Combine(dir, name);
            PlistWriter.WriteFile(cfg, path);
            var r = OcValidate.Run(Path.Combine(oc!, "Utilities", "ocvalidate", "ocvalidate.exe"), path);
            Assert.True(r.Ok, $"{profile}/{name}: ocvalidate exit {r.ExitCode}\n{r.Output}");
        }
    }
}
