using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Reports;

namespace HackWizard.App.ViewModels;

public sealed class FinalViewModel : StepViewModel
{
    public FinalViewModel(WizardContext ctx) : base(ctx) => RestartToBiosCommand = new RelayCommand(RestartToBios);
    public override string Title => "9. Install macOS";
    public override string Explanation => "Your USB drive is ready. Follow this checklist exactly. Keep this page (or the copy on the USB) open on your phone.";
    public RelayCommand RestartToBiosCommand { get; }

    private BiosGuide? Guide => Ctx.Hardware is { } hw ? BiosGuideGenerator.Build(Ctx.Kb, hw, Ctx.Compat) : null;

    public string[] Checklist
    {
        get
        {
            var g = Guide;
            var setup = g?.SetupKey ?? "the setup key"; var boot = g?.BootMenuKey ?? "the boot-menu key";
            var wired = Ctx.Compat?.Capabilities.Contains("net-install") == true ? "Plug in an Ethernet cable" : "Connect a USB-Ethernet adapter (or an iPhone with USB hotspot)";
            return new[]
            {
                $"1. {wired}: the installer downloads macOS from Apple and Wi-Fi is not available in the installer.",
                "2. Insert the HackWizard USB drive.",
                $"3. Click 'Restart into BIOS' below (or press {setup} at power-on), apply the settings from step 8, then save and exit.",
                $"4. When the PC restarts, press {boot} repeatedly and choose the USB drive (often listed as 'UEFI: <USB name>').",
                "5. In the OpenCore picker select the macOS Base System / Install macOS entry (press Space if you don't see it). Lots of scrolling text is normal.",
                "6. In macOS Utilities open Disk Utility → View → Show All Devices → select the internal disk (top level) → Erase: Name 'Macintosh HD', Format APFS, Scheme GUID Partition Map. THIS ERASES WINDOWS.",
                "7. Quit Disk Utility, choose Reinstall macOS and select 'Macintosh HD'. The PC restarts several times: each time pick 'macOS Installer', later 'Macintosh HD', in OpenCore.",
                "8. After setup, run Utilities/Copy-EFI-To-Internal.command from the USB so the PC boots without the USB.",
                "9. Follow Utilities/Post-Install-Guide.html (USB port map, Wi-Fi/TRIM steps for your hardware, release config)."
            };
        }
    }

    public override bool CanProceed => true;
    public override Task OnEnterAsync() { OnPropertyChanged(nameof(Checklist)); return Task.CompletedTask; }

    private void RestartToBios()
    {
        if (!Ctx.Dialogs.Confirm("Restart into BIOS?", "Save your work. Windows will restart directly into the firmware setup in 5 seconds.\n\nContinue?")) return;
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /fw /t 5") { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { Ctx.Dialogs.Error("Restart failed", ex.Message + $"\nRestart manually and press {Guide?.SetupKey ?? "the setup key"} at power-on."); }
    }
}
