using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Preflight;

namespace HackWizard.App.ViewModels;

public sealed class PreflightViewModel : StepViewModel
{
    public PreflightViewModel(WizardContext ctx) : base(ctx) => RunCommand = new AsyncRelayCommand(RunAsync);
    public override string Title => "1. Preflight checks";
    public override string Explanation =>
        "HackWizard checks that this PC and Windows are ready: processor type, administrator rights, internet, free space, UEFI firmware, Secure Boot, BitLocker, TPM and power. Nothing is changed in this step.";

    public ObservableCollection<PreflightCheck> Checks { get; } = new();
    public AsyncRelayCommand RunCommand { get; }

    public string LegalNotice =>
        "Apple's macOS Software License Agreement permits installing macOS only on Apple-branded computers. Installing it on this PC may violate that license. " +
        "HackWizard is free, open-source software provided without warranty. It downloads macOS directly from Apple's servers and open-source tools from their official projects; it does not distribute macOS. " +
        "The installation ERASES the whole internal disk (including Windows and your files). You accept full responsibility for any data loss, hardware issues or license violations.";

    private bool _accepted;
    public bool LegalAccepted { get => _accepted; set { if (SetProperty(ref _accepted, value)) RaiseState(); } }
    public override bool CanProceed => Ctx.Preflight is { HasBlockingFailure: false } && LegalAccepted;
    public override Task OnEnterAsync() => Ctx.Preflight is null ? RunAsync() : Task.CompletedTask;

    private Task RunAsync() => RunGuarded("Running checks", async ct =>
    {
        Checks.Clear();
        var r = await new PreflightChecker().RunAsync(ct);
        Ctx.Preflight = r;
        foreach (var c in r.Checks) Checks.Add(c);
        Status = r.HasBlockingFailure ? "Fix the items marked ✖, then click Re-run." : "All required checks passed.";
    });
}
