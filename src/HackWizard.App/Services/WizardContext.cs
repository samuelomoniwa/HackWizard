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
    public IReadOnlyList<GeneratedSsdt> Ssdts { get; set; } = Array.Empty<GeneratedSsdt>();
    public AcpiDumpResult? AcpiDump { get; set; }
    public DownloadSet? Downloads { get; set; }
    public BuildResult? Build { get; set; }
    public string? LastUsbResult { get; set; }
    public UsbDisk? WrittenDisk { get; set; }

    private CancellationTokenSource? _cts;
    public CancellationToken NewOperation() { _cts?.Dispose(); _cts = new CancellationTokenSource(); return _cts.Token; }
    public void CancelOperation() => _cts?.Cancel();
}
