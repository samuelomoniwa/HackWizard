using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Plist;

namespace HackWizard.App.ViewModels;

public sealed class EfiViewModel : StepViewModel
{
    public EfiViewModel(WizardContext ctx) : base(ctx)
    {
        BuildCommand = new AsyncRelayCommand(BuildAsync);
        CheckCoverageCommand = new RelayCommand(() => Ctx.Dialogs.OpenPath("https://checkcoverage.apple.com/"));
        OpenFolderCommand = new RelayCommand(() => { if (Ctx.Build != null) Ctx.Dialogs.OpenPath(Ctx.Build.StagingDir); });
    }
    public override string Title => "7. Build the OpenCore EFI";
    public override string Explanation =>
        "Builds EFI/OC/config.plist from OpenCore's own Sample.plist (never a third-party EFI), applying the Dortania settings for your CPU generation, " +
        "kexts in the correct load order, graphics and audio properties, and a freshly generated serial for your Mac model. OpenCore's ocvalidate must report no errors before you can continue.";
    public AsyncRelayCommand BuildCommand { get; }
    public RelayCommand CheckCoverageCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public ObservableCollection<string> KextOrder { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    private string _validation = ""; public string Validation { get => _validation; private set => SetProperty(ref _validation, value); }
    private string _diff = ""; public string Diff { get => _diff; private set => SetProperty(ref _diff, value); }
    private string _identity = ""; public string Identity { get => _identity; private set => SetProperty(ref _identity, value); }
    public override bool CanProceed => Ctx.Build is { Ok: true };
    public override Task OnEnterAsync() => Ctx.Build is null ? BuildAsync() : Task.CompletedTask;

    private Task BuildAsync() => RunGuarded("Building EFI", async ct =>
    {
        KextOrder.Clear(); Notes.Clear(); Validation = Diff = Identity = "";
        var r = await Task.Run(() => EfiPipeline.Build(Ctx), ct);
        Ctx.Build = r;
        var i = 1;
        foreach (var k in r.Kexts) KextOrder.Add($"{i++,2}. {k.BundlePath}{(k.Enabled ? "" : "  (" + k.Comment + ")")}  [{k.ExecutablePath}]");
        foreach (var n in r.Notes) Notes.Add(n);
        Validation = $"config.plist: {(r.Validation.Ok ? "OK" : "ERRORS")}\n{r.Validation.Output}\n\nconfig-release.plist: {(r.ReleaseValidation.Ok ? "OK" : "ERRORS")}\n{r.ReleaseValidation.Output}";
        Diff = PlistDiff.ToText(r.Diff);
        Identity = $"SMBIOS {r.Identity.Model}\nSerial {r.Identity.Serial}\nMLB {r.Identity.Mlb}\nSystemUUID {r.Identity.SystemUuid}\n" +
                   "Check that the serial is NOT valid on Apple's coverage page (\"Please enter a valid serial number\"). If it is valid, click Rebuild.";
        Status = r.Ok ? "EFI built and validated by ocvalidate." : "ocvalidate reported errors — HackWizard will not continue. Please report this with Copy diagnostics.";
    });
}
