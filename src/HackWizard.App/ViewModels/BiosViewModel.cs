using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Bios;
using HackWizard.Core.Logging;
using HackWizard.Core.Reports;
using HackWizard.Core.Rules;

namespace HackWizard.App.ViewModels;

public sealed class BiosViewModel : StepViewModel
{
    public BiosViewModel(WizardContext ctx) : base(ctx)
    {
        OpenHtmlCommand = new RelayCommand(() => { if (_html != null) Ctx.Dialogs.OpenPath(_html); });
        ReadLenovoCommand = new AsyncRelayCommand(ReadLenovoAsync);
        ApplyLenovoCommand = new AsyncRelayCommand<object?>(ApplyLenovoAsync);
    }
    public override string Title => "9. Firmware (BIOS/UEFI) settings";
    public override string Explanation =>
        "These firmware settings must be changed before the USB will boot. The list is tailored to your PC (maker, model, CPU, laptop/desktop). " +
        "On Lenovo ThinkPads HackWizard can optionally apply supported settings through Lenovo's official WMI interface (off by default; you see every change first).";
    public BiosGuide? Guide { get; private set; }
    public ObservableCollection<KbBiosSetting> Settings { get; } = new();
    public ObservableCollection<BiosSettingChange> Changes { get; } = new();
    public RelayCommand OpenHtmlCommand { get; }
    public AsyncRelayCommand ReadLenovoCommand { get; }
    public AsyncRelayCommand<object?> ApplyLenovoCommand { get; }
    public bool LenovoAvailable { get; private set; }
    private bool _optIn; public bool LenovoOptIn { get => _optIn; set => SetProperty(ref _optIn, value); }
    private string _lenovoLog = ""; public string LenovoLog { get => _lenovoLog; private set => SetProperty(ref _lenovoLog, value); }
    private string? _html;
    public string Header => Guide is null ? "" : $"{Guide.Title}\nSetup key: {Guide.SetupKey}    Boot menu: {Guide.BootMenuKey}\n{Guide.SetupNotes}";
    public override bool CanProceed => Guide != null;

    public override Task OnEnterAsync()
    {
        Guide = BiosGuideGenerator.Build(Ctx.Kb, Ctx.Hardware!, Ctx.Compat);
        Settings.Clear(); foreach (var s in Guide.Settings) Settings.Add(s);
        _html = Path.Combine(AppPaths.Reports, "BIOS-Guide.html");
        File.WriteAllText(_html, BiosGuideGenerator.ToHtml(Guide), new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(AppPaths.Reports, "BIOS-Guide.pdf"), SimplePdf.FromBiosGuide(Guide));
        LenovoAvailable = Ctx.Hardware!.System.Manufacturer.Equals("LENOVO", StringComparison.OrdinalIgnoreCase) && LenovoWmiBios.IsAvailable() && Guide.Settings.Any(s => s.LenovoWmi != null);
        OnPropertyChanged(nameof(Guide)); OnPropertyChanged(nameof(Header)); OnPropertyChanged(nameof(LenovoAvailable));
        Status = "Follow the steps below after restarting into the firmware (see the final step). A copy is on the USB under Utilities.";
        RaiseState();
        return Task.CompletedTask;
    }

    private Task ReadLenovoAsync() => RunGuarded("Reading ThinkPad BIOS settings", async ct =>
    {
        if (!LenovoOptIn) throw new InvalidOperationException("Tick the opt-in box first.");
        var bios = new LenovoWmiBios();
        var (plan, sup) = await Task.Run(() => (bios.Plan(Guide!.Settings), bios.SupervisorPasswordSet()), ct);
        Changes.Clear(); foreach (var c in plan) Changes.Add(c);
        Status = plan.Count == 0 ? "No supported settings were found through WMI on this BIOS — use the manual guide." :
            $"{plan.Count(c => c.NeedsChange)} setting(s) differ from the recommended value. Review, untick anything you don't want, then Apply." + (sup ? " A supervisor password is set: enter it below." : "");
    });

    private Task ApplyLenovoAsync(object? passwordBox) => RunGuarded("Applying ThinkPad BIOS settings", async ct =>
    {
        if (!LenovoOptIn) throw new InvalidOperationException("Opt-in is required.");
        var selected = Changes.Where(c => c.Selected && c.NeedsChange).ToList();
        if (selected.Count == 0) { Status = "Nothing to change."; return; }
        var summary = string.Join("\n", selected.Select(c => $"• {c.Title} ({c.WmiName}): {c.Current} → {c.Target}"));
        if (!Ctx.Dialogs.Confirm("Change BIOS settings?", $"HackWizard will change these firmware settings via Lenovo WMI:\n\n{summary}\n\nThey take effect after a restart. If BitLocker is on, Windows may ask for your recovery key. Continue?", dangerous: true))
        { Status = "Cancelled — nothing changed."; return; }
        var pwd = (passwordBox as PasswordBox)?.Password;
        LenovoLog = await Task.Run(() => new LenovoWmiBios().Apply(selected, pwd), ct);
        Status = "BIOS settings saved. They apply on next restart.";
    });
}
