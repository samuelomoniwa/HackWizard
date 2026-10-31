using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Logging;
using HackWizard.Core.Reports;
using HackWizard.Core.Rules;

namespace HackWizard.App.ViewModels;

public sealed class CompatibilityViewModel : StepViewModel
{
    public CompatibilityViewModel(WizardContext ctx) : base(ctx) => UpdateKbCommand = new AsyncRelayCommand(UpdateKbAsync);
    public override string Title => "3. Compatibility report";
    public override string Explanation =>
        "Each detected component is checked against HackWizard's open knowledge base. Green = works, amber = works with limitations, red = does not work in macOS. " +
        "HackWizard picks the Mac model (SMBIOS) that matches your hardware and recommends the newest macOS that supports it; you can choose another (with warnings).";

    public ObservableCollection<CompatItem> Items { get; } = new();
    public ObservableCollection<KbMacosVersion> MacosVersions { get; } = new();
    public AsyncRelayCommand UpdateKbCommand { get; }
    public string KbVersion => $"Knowledge base {Ctx.Kb.Manifest.KbVersion}";
    public string SmbiosText => Ctx.Compat is { Smbios.Length: > 0 } c ? $"Mac model (SMBIOS): {c.Smbios}" : "";
    public string WarningsText => Ctx.Compat is { Warnings.Count: > 0 } c ? "• " + string.Join("\n• ", c.Warnings) : "";

    private KbMacosVersion? _selected;
    public KbMacosVersion? SelectedMacos
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { Ctx.SelectedMacos = value; Ctx.Downloads = null; Ctx.Build = null; OnPropertyChanged(nameof(MacosWarning)); RaiseState(); } }
    }

    public string BlockingText => Ctx.Compat is { IsBlocked: true } c ? "HackWizard cannot build a working setup for this PC:\n• " + string.Join("\n• ", c.BlockingReasons) : "";
    public bool IsBlocked => Ctx.Compat?.IsBlocked ?? false;

    public string MacosWarning
    {
        get
        {
            if (Ctx.Compat is not { } c || SelectedMacos is null) return "";
            var lines = new List<string>();
            if (SelectedMacos.Id != c.RecommendedMacos) lines.Add($"Not the recommended version ({c.RecommendedMacos}).");
            if (c.MacosBlocks.TryGetValue(SelectedMacos.Id, out var b)) lines.AddRange(b.Select(x => "NOT POSSIBLE: " + x));
            if (c.MacosWarnings.TryGetValue(SelectedMacos.Id, out var w)) lines.AddRange(w);
            lines.AddRange(SelectedMacos.Notes);
            return string.Join("\n", lines);
        }
    }

    public bool ShowRealCpu
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

    public override bool CanProceed => Ctx.Compat is { IsBlocked: false } c && SelectedMacos != null && !c.MacosBlocks.ContainsKey(SelectedMacos.Id);
    public override Task OnEnterAsync() { Evaluate(); return Task.CompletedTask; }

    private void Evaluate()
    {
        var c = new RulesEngine(Ctx.Kb).Evaluate(Ctx.Hardware!, new EvaluationContext { IsUefi = Ctx.Preflight?.IsUefi ?? true });
        Ctx.Compat = c; Ctx.Downloads = null; Ctx.Build = null;
        Items.Clear();
        foreach (var item in c.Items.Where(x => x.Device != "-").OrderBy(x => x.Status == CompatStatus.Info).ThenBy(x => x.Component)) Items.Add(item);
        MacosVersions.Clear();
        foreach (var v in Ctx.Kb.Macos.Versions) MacosVersions.Add(v);
        SelectedMacos = Ctx.SelectedMacos is { } prev && MacosVersions.Contains(prev) && !c.MacosBlocks.ContainsKey(prev.Id) ? prev : MacosVersions.FirstOrDefault(v => v.Id == c.RecommendedMacos);
        ReportWriter.WriteHtml(Ctx.Hardware!, c, Path.Combine(AppPaths.Reports, "hardware-report.html"));
        foreach (var p in new[] { nameof(BlockingText), nameof(IsBlocked), nameof(MacosWarning), nameof(KbVersion), nameof(SmbiosText), nameof(WarningsText), nameof(RealCpuLabel), nameof(MacCpuLabel) }) OnPropertyChanged(p);
        Status = c.IsBlocked ? "This PC cannot continue — see the red box." : $"{c.Smbios}; recommended {c.RecommendedMacos}; {c.EffectiveKexts.Count()} kexts, {c.Ssdts.Count} SSDTs planned.";
        RaiseState();
    }

    private Task UpdateKbAsync() => RunGuarded("Checking for knowledge-base updates", async ct =>
    {
        using var http = EfiPipeline.CreateHttp();
        var msg = await KnowledgeBaseLoader.UpdateAsync(http, Ctx.Kb, ct);
        if (msg.StartsWith("Knowledge base updated", StringComparison.Ordinal)) { Ctx.Kb = KnowledgeBaseLoader.LoadOrSeed(); Evaluate(); }
        Status = msg;
    });
}
