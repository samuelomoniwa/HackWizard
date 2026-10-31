using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Logging;
using HackWizard.DiskWriter;

namespace HackWizard.App.ViewModels;

public sealed class UsbViewModel : StepViewModel
{
    public UsbViewModel(WizardContext ctx) : base(ctx)
    {
        RefreshCommand = new RelayCommand(Refresh);
        WriteCommand = new AsyncRelayCommand(WriteAsync);
    }
    public override string Title => "7. Write the USB drive";
    public override string Explanation =>
        "Insert a USB drive of 8 GB or larger. EVERYTHING on it will be erased. Only USB drives are listed — internal disks and the Windows disk are never shown. " +
        "Use 'Dry run' to see what would happen, or 'Simulate' to write to an image file instead of a real drive.";
    public ObservableCollection<UsbDisk> Disks { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand WriteCommand { get; }

    private UsbDisk? _sel; public UsbDisk? SelectedDisk { get => _sel; set { SetProperty(ref _sel, value); ConfirmNumber = ""; } }
    private string _confirm = ""; public string ConfirmNumber { get => _confirm; set => SetProperty(ref _confirm, value); }
    private bool _dry = true; public bool DryRun { get => _dry; set { if (SetProperty(ref _dry, value) && value) { Simulate = false; RealWrite = false; } } }
    private bool _sim; public bool Simulate { get => _sim; set { if (SetProperty(ref _sim, value) && value) { DryRun = false; RealWrite = false; } } }
    private bool _real; public bool RealWrite { get => _real; set { if (SetProperty(ref _real, value) && value) { DryRun = false; Simulate = false; } } }
    private string _imagePath = Path.Combine(AppPaths.Work, "hackwizard-usb.img"); public string ImagePath { get => _imagePath; set => SetProperty(ref _imagePath, value); }
    private string _log = ""; public string Log { get => _log; private set => SetProperty(ref _log, value); }
    private bool _realDone;
    public override bool CanProceed => _realDone || Ctx.LastUsbResult != null;
    public override Task OnEnterAsync() { Refresh(); return Task.CompletedTask; }

    private void Refresh()
    {
        Disks.Clear();
        foreach (var d in UsbDisks.List()) Disks.Add(d);
        Status = Disks.Count == 0 ? "No suitable USB drive found (USB bus, ≥ 8 GB). Insert one and click Refresh." : $"{Disks.Count} USB drive(s) found.";
    }

    private Task WriteAsync() => RunGuarded("Writing", async ct =>
    {
        var build = Ctx.Build ?? throw new InvalidOperationException("Build the EFI first.");
        var mode = RealWrite ? WriteMode.RealDisk : Simulate ? WriteMode.SimulateImage : WriteMode.DryRun;
        if (mode == WriteMode.RealDisk)
        {
            var d = SelectedDisk ?? throw new InvalidOperationException("Select a USB drive.");
            if (ConfirmNumber.Trim() != d.Number.ToString()) throw new InvalidOperationException($"Type the disk number ({d.Number}) in the confirmation box to continue.");
            if (!Ctx.Dialogs.Confirm("Erase USB drive?", $"ALL DATA on this drive will be permanently erased:\n\n{d.Display}\n\nContinue?", dangerous: true)) { Status = "Cancelled."; return; }
        }
        EfiPipeline.CopyLogs(build.StagingDir);
        var result = await new UsbWriter().WriteAsync(build.StagingDir, new WriteOptions { Mode = mode, Target = SelectedDisk, ImagePath = ImagePath },
            new Progress<WriteProgress>(p => { Progress = p.Percent; Status = $"{p.Stage} {p.Detail}"; }), ct);
        Log = result;
        Ctx.LastUsbResult = result;
        if (mode == WriteMode.RealDisk) { _realDone = true; Ctx.WrittenDisk = SelectedDisk; }
        Status = mode switch
        {
            WriteMode.RealDisk => "USB drive ready. Continue to the BIOS guide.",
            WriteMode.SimulateImage => "Simulation finished. Choose 'Write to USB drive' when ready.",
            _ => "Dry run finished. Choose 'Write to USB drive' when ready."
        };
        Progress = 100;
    });
}
