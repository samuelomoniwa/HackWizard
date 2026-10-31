using HackWizard.Core.Acpi;
using HackWizard.Core.Models;
using HackWizard.Core.Preflight;
using HackWizard.Core.Rules;
using HackWizard.DiskWriter;

namespace HackWizard.App.Services;

/// <summary>State shared by all wizard steps.</summary>
public sealed class WizardContext
{
    public required KnowledgeBase Kb { get; set; }
    public required IDialogService Dialogs { get; init; }
    public PreflightResult? Preflight { get; set; }
    public HardwareReport? Hardware { get; set; }
    public CompatibilityResult? Compat { get; set; }
    public KbMacosVersion? SelectedMacos { get; set; }
    /// <summary>true = About This Mac shows the Mac model's Intel CPU name; false = the real CPU name.</summary>
    public bool ShowMacCpuName { get; set; }
    public IReadOnlyList<GeneratedSsdt> Ssdts { get; set; } = Array.Empty<GeneratedSsdt>();
    /// <summary>USB map from step 5; null or empty selection = no map (UTBDefault.kext is used).</summary>
    public HackWizard.Core.Usb.UsbMapState? UsbMap { get; set; }
    public AcpiDumpResult? AcpiDump { get; set; }
    public DownloadSet? Downloads { get; set; }
    public BuildResult? Build { get; set; }
    public string? LastUsbResult { get; set; }
    public UsbDisk? WrittenDisk { get; set; }

    private CancellationTokenSource? _cts;
    public CancellationToken NewOperation() { _cts?.Dispose(); _cts = new CancellationTokenSource(); return _cts.Token; }
    public void CancelOperation() => _cts?.Cancel();
}
