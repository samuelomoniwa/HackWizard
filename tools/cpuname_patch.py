import json, pathlib, sys
R = pathlib.Path(".")
def patch(path, old, new):
    p = R / path
    s = p.read_text(encoding="utf-8-sig")
    if new.strip() in s:
        print("already patched:", path); return
    if old not in s:
        sys.exit(f"ANCHOR NOT FOUND in {path}:\n{old}")
    p.write_text(s.replace(old, new, 1), encoding="utf-8")
    print("patched:", path)

# 1) Knowledge-base model: SMBIOS -> Apple CPU name table
patch("src/HackWizard.Core/Rules/KnowledgeBase.cs",
"    public List<KbMacosVersion> Versions { get; set; } = new();",
"""    public List<KbMacosVersion> Versions { get; set; } = new();
    /// <summary>SMBIOS model -> processor name of the real Mac (used for the optional 'Mac model CPU name').</summary>
    public Dictionary<string, string> SmbiosCpuNames { get; set; } = new();""")

# 2) Wizard state
patch("src/HackWizard.App/Services/WizardContext.cs",
"    public KbMacosVersion? SelectedMacos { get; set; }",
"""    public KbMacosVersion? SelectedMacos { get; set; }
    /// <summary>true = About This Mac shows the Mac model's Intel CPU name; false = the real CPU name.</summary>
    public bool ShowMacCpuName { get; set; }""")

# 3) Compatibility step: the two options
patch("src/HackWizard.App/ViewModels/CompatibilityViewModel.cs",
"    public override bool CanProceed => Ctx.Compat is { IsBlocked: false } c",
"""    public bool ShowRealCpu
    {
        get => !Ctx.ShowMacCpuName;
        set { if (value && Ctx.ShowMacCpuName) { Ctx.ShowMacCpuName = false; Ctx.Build = null; OnPropertyChanged(nameof(ShowRealCpu)); OnPropertyChanged(nameof(ShowMacCpu)); } }
    }
    public bool ShowMacCpu
    {
        get => Ctx.ShowMacCpuName;
        set { if (value && !Ctx.ShowMacCpuName) { Ctx.ShowMacCpuName = true; Ctx.Build = null; OnPropertyChanged(nameof(ShowRealCpu)); OnPropertyChanged(nameof(ShowMacCpu)); } }
    }
    public string RealCpuLabel => $"Show my real processor: {Ctx.Hardware?.Cpu.Name}";
    public string MacCpuLabel => EfiPipeline.MacCpuName(Ctx.Kb, Ctx.Compat?.Smbios) is { Length: > 0 } n
        ? $"Show the Mac model's processor: {n} (as in a real {Ctx.Compat!.Smbios})"
        : "Show the Mac model's processor (not available for this Mac model)";

    public override bool CanProceed => Ctx.Compat is { IsBlocked: false } c""")
patch("src/HackWizard.App/ViewModels/CompatibilityViewModel.cs",
"nameof(SmbiosText), nameof(WarningsText) })",
"nameof(SmbiosText), nameof(WarningsText), nameof(RealCpuLabel), nameof(MacCpuLabel) })")

# 4) UI
patch("src/HackWizard.App/Views/StepTemplates.xaml",
"""            <TextBlock Text="{Binding MacosWarning}" Foreground="{DynamicResource Brush.Warn}" Margin="0,8,0,0"/>""",
"""            <TextBlock Text="{Binding MacosWarning}" Foreground="{DynamicResource Brush.Warn}" Margin="0,8,0,0"/>
            <TextBlock Text="Processor name shown in About This Mac" Style="{StaticResource H2}"/>
            <RadioButton GroupName="cpuname" IsChecked="{Binding ShowRealCpu}" Content="{Binding RealCpuLabel}" Margin="0,0,0,4"/>
            <RadioButton GroupName="cpuname" IsChecked="{Binding ShowMacCpu}" Content="{Binding MacCpuLabel}"/>
            <TextBlock Text="Cosmetic only (via RestrictEvents): the real core count, speed and CPU features are unchanged, and apps still see your real processor."
                       Foreground="{DynamicResource Brush.Muted}" Margin="0,4,0,0"/>""")

# 5) EFI build: always include RestrictEvents, apply the name to both configs
patch("src/HackWizard.App/Services/EfiPipeline.cs",
"        var kb = ctx.Kb; var c = ctx.Compat!; var macos = ctx.SelectedMacos!;",
"""        var kb = ctx.Kb; var c = ctx.Compat!; var macos = ctx.SelectedMacos!;
        if (!c.Kexts.Contains("RestrictEvents.kext", StringComparer.OrdinalIgnoreCase)) c.Kexts.Add("RestrictEvents.kext");""")
patch("src/HackWizard.App/Services/EfiPipeline.cs",
"""        var cfgPath = Path.Combine(oc, "config.plist");""",
"""        if (ctx.ShowMacCpuName && MacCpuName(ctx.Kb, c.Smbios) is { Length: > 0 } cpuName)
            foreach (var cfg in new[] { outputs.Debug, outputs.Release })
                ApplyCpuName(cfg, cpuName, forceForIntel: hw.Cpu.Vendor == "GenuineIntel");
        var cfgPath = Path.Combine(oc, "config.plist");""")
patch("src/HackWizard.App/Services/EfiPipeline.cs",
"    public static void CopyLogs(string stagingDir)",
"""    public const string RestrictEventsGuid = "4D1FDA02-38C7-4A6A-9CC6-4BCCA8B30102";

    public static string MacCpuName(KnowledgeBase kb, string? smbios) =>
        smbios != null && kb.Macos.SmbiosCpuNames.TryGetValue(smbios, out var n) ? n : "";

    /// <summary>Sets RestrictEvents' custom CPU brand string (NVRAM revcpuname). Intel CPUs also need revcpu=1.</summary>
    public static void ApplyCpuName(PDict cfg, string name, bool forceForIntel)
    {
        var nv = cfg.Dict("NVRAM");
        var add = nv.Dict("Add");
        if (add[RestrictEventsGuid] is not PDict d) add[RestrictEventsGuid] = d = new PDict();
        d.Set("revcpuname", new PString(name));
        var del = nv.Dict("Delete");
        if (del[RestrictEventsGuid] is not PArray a) del[RestrictEventsGuid] = a = new PArray();
        if (!a.OfType<PString>().Any(s => s.Value == "revcpuname")) a.Add(new PString("revcpuname"));
        if (forceForIntel)
        {
            var apple = add.Dict(ConfigBuilder.AppleVendorGuid);
            var args = (apple.Str("boot-args") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            RulesEngine.AddBootArg(args, "revcpu=1");
            apple.Set("boot-args", string.Join(' ', args));
        }
    }

    public static void CopyLogs(string stagingDir)""")

# 6) Knowledge base: Apple CPU for every SMBIOS HackWizard can choose
mp = R / "Rules/kb/macos.json"
m = json.loads(mp.read_text(encoding="utf-8-sig"))
m["smbiosCpuNames"] = {
  "MacBookPro11,4": "Intel Core i7-4770HQ", "MacBookPro12,1": "Intel Core i5-5257U",
  "MacBookPro13,1": "Intel Core i5-6360U",  "MacBookPro13,3": "Intel Core i7-6700HQ",
  "MacBookPro14,1": "Intel Core i5-7360U",  "MacBookPro14,3": "Intel Core i7-7820HQ",
  "MacBookPro15,1": "Intel Core i7-8750H",  "MacBookPro15,2": "Intel Core i5-8259U",
  "MacBookPro16,1": "Intel Core i7-9750H",  "MacBookPro16,2": "Intel Core i5-1038NG7",
  "MacBookPro16,3": "Intel Core i5-8257U",  "MacBookAir9,1": "Intel Core i3-1000NG4",
  "iMac16,2": "Intel Core i5-5675R", "iMac17,1": "Intel Core i5-6500", "iMac18,1": "Intel Core i5-7360U",
  "iMac18,3": "Intel Core i5-7500",  "iMac19,1": "Intel Core i5-8500", "iMac20,1": "Intel Core i5-10500",
  "MacPro7,1": "Intel Xeon W-3223"
}
mp.write_text(json.dumps(m, indent=2) + "\n", encoding="utf-8", newline="\n")
print("patched: Rules/kb/macos.json")
print("ALL DONE")
