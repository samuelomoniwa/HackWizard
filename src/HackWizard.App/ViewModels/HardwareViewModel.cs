using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Detection;
using HackWizard.Core.Logging;
using HackWizard.Core.Reports;

namespace HackWizard.App.ViewModels;

public sealed record InfoRow(string Category, string Name, string Detail);

public sealed class HardwareViewModel : StepViewModel
{
    public HardwareViewModel(WizardContext ctx) : base(ctx)
    {
        DetectCommand = new AsyncRelayCommand(DetectAsync);
        OpenReportCommand = new RelayCommand(() => { if (_htmlPath != null) Ctx.Dialogs.OpenPath(_htmlPath); });
    }
    public override string Title => "2. Hardware detection";
    public override string Explanation => "Reads (never changes) your hardware: maker, model and BIOS, CPU, graphics, network, disks, audio codec, keyboard/trackpad and more. A JSON and HTML report is saved for troubleshooting — and for contributing support for new PCs.";
    public ObservableCollection<InfoRow> Rows { get; } = new();
    public AsyncRelayCommand DetectCommand { get; }
    public RelayCommand OpenReportCommand { get; }
    private string? _htmlPath;
    public override bool CanProceed => Ctx.Hardware != null;
    public override Task OnEnterAsync() => Ctx.Hardware is null ? DetectAsync() : Task.CompletedTask;

    private Task DetectAsync() => RunGuarded("Detecting hardware", async ct =>
    {
        Rows.Clear();
        var hw = await new HardwareDetector(Ctx.Kb.LookupCodename).DetectAsync(new Progress<string>(s => Status = s), ct);
        Ctx.Hardware = hw; Ctx.Compat = null; Ctx.Build = null; Ctx.Downloads = null;
        var s = hw.System;
        Rows.Add(new("System", $"{s.Manufacturer} {s.Version}", $"Model {s.Model}{(s.MachineType.Length > 0 ? ", machine type " + s.MachineType : "")}, {(s.IsLaptop ? "laptop" : "desktop")}, board {s.Baseboard}"));
        Rows.Add(new("BIOS", s.BiosVendor, $"{s.BiosVersion} ({s.BiosDate})"));
        Rows.Add(new("CPU", hw.Cpu.Name, $"{hw.Cpu.Codename}; {hw.Cpu.Vendor} family 0x{hw.Cpu.Family:X} model 0x{hw.Cpu.Model:X} stepping {hw.Cpu.Stepping}; {hw.Cpu.PhysicalCores} cores / {hw.Cpu.LogicalProcessors} threads; {hw.HostArchitecture}"));
        Rows.Add(new("Memory", $"{s.MemoryBytes / 1024 / 1024 / 1024} GB", $"Display {s.DisplayWidth}×{s.DisplayHeight}"));
        Rows.Add(new("Input", "Trackpad interface", hw.TrackpadInterface));
        foreach (var d in hw.Devices.OrderBy(x => x.Category).ThenBy(x => x.Name))
            Rows.Add(new(d.Category.ToString(), d.Name, string.Join("  ", new[] { d.PciIdKey, d.AcpiPath, d.OcDevicePath }.Where(x => !string.IsNullOrEmpty(x)))));
        foreach (var a in hw.AcpiDevices) Rows.Add(new("ACPI", a.Hid, a.AcpiPath));
        foreach (var d in hw.Disks) Rows.Add(new("Disk", d.Model, $"#{d.Number} {d.BusType} {d.SizeBytes / 1_000_000_000} GB{(d.IsSystem ? " (system)" : "")}"));
        foreach (var w in hw.Warnings) Rows.Add(new("Warning", w, ""));
        ReportWriter.WriteJson(hw, Path.Combine(AppPaths.Reports, "hardware-report.json"));
        _htmlPath = ReportWriter.WriteHtml(hw, null, Path.Combine(AppPaths.Reports, "hardware-report.html"));
        Status = $"Detected {hw.Devices.Count} devices and {hw.Disks.Count} disks. Reports saved to {AppPaths.Reports}.";
    });
}
