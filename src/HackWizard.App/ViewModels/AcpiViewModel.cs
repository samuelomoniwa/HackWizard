using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Acpi;
using HackWizard.Core.Logging;

namespace HackWizard.App.ViewModels;

public sealed class AcpiViewModel : StepViewModel
{
    public AcpiViewModel(WizardContext ctx) : base(ctx)
    {
        RunCommand = new AsyncRelayCommand(RunAsync);
        OpenDumpCommand = new RelayCommand(() => { if (Ctx.AcpiDump != null) Ctx.Dialogs.OpenPath(Ctx.AcpiDump.Directory); });
    }
    public override string Title => "4. ACPI tables and SSDTs";
    public override string Explanation =>
        "ACPI tables describe your PC's hardware to the operating system. HackWizard saves a copy of your firmware's tables (for troubleshooting) and generates the small " +
        "patch tables (SSDTs) macOS needs on this machine, using the real ACPI paths read from Windows (Dortania's 'Getting Started with ACPI').";
    public ObservableCollection<GeneratedSsdt> Ssdts { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand OpenDumpCommand { get; }
    private bool _done;
    public override bool CanProceed => _done;
    public override Task OnEnterAsync() => _done && Ctx.Build != null ? Task.CompletedTask : RunAsync();

    private Task RunAsync() => RunGuarded("Dumping ACPI and generating SSDTs", async ct =>
    {
        Ssdts.Clear(); Notes.Clear(); _done = false;
        var dir = Path.Combine(AppPaths.Work, "ACPI-Dump");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var dump = await Task.Run(() => AcpiDumper.Dump(dir, null), ct);
        Ctx.AcpiDump = dump;
        Notes.Add($"Dumped {dump.Files.Count} tables to {dump.Directory}");
        foreach (var n in dump.Notes) Notes.Add(n);
        var gen = new SsdtGenerator();
        var list = gen.Generate(Ctx.Compat!.Ssdts, Ctx.Hardware!);
        foreach (var w in gen.Warnings) Notes.Add("⚠ " + w);
        foreach (var s in list)
        {
            if (!Aml.ChecksumValid(s.Aml)) throw new InvalidOperationException($"{s.Name}: AML checksum invalid.");
            Ssdts.Add(s);
        }
        Ctx.Ssdts = list; Ctx.Build = null;
        _done = true;
        Status = list.Count == 0 ? "No SSDTs needed." : $"{list.Count} SSDTs generated: {string.Join(", ", list.Select(s => s.Name))}";
    });
}
