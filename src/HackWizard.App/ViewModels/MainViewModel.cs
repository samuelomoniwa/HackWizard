using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Logging;
using HackWizard.Core.Rules;

namespace HackWizard.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<StepViewModel> Steps { get; }
    public WizardContext Ctx { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    private int _index;
    public int Index
    {
        get => _index;
        private set
        {
            if (_index != value) Steps[_index].OnLeave();
            if (!SetProperty(ref _index, value)) return;
            OnPropertyChanged(nameof(Current)); OnPropertyChanged(nameof(StepLabel)); OnPropertyChanged(nameof(OverallProgress));
            Refresh();
            _ = Current.OnEnterAsync();
        }
    }

    public StepViewModel Current => Steps[Index];
    public string StepLabel => $"Step {Index + 1} of {Steps.Count}";
    public double OverallProgress => (Index + 1) * 100.0 / Steps.Count;
    public string NextText => Index == Steps.Count - 1 ? "Finish" : "Next ›";

    public MainViewModel(WizardContext ctx)
    {
        Ctx = ctx;
        Steps = new ObservableCollection<StepViewModel>
        {
            new PreflightViewModel(ctx), new HardwareViewModel(ctx), new CompatibilityViewModel(ctx), new AcpiViewModel(ctx), new UsbMapViewModel(ctx),
            new DownloadsViewModel(ctx), new EfiViewModel(ctx), new UsbViewModel(ctx), new BiosViewModel(ctx), new FinalViewModel(ctx)
        };
        foreach (var s in Steps) s.StateChanged += Refresh;
        NextCommand = new RelayCommand(() => { if (Index < Steps.Count - 1) Index++; else Application.Current.Shutdown(); }, () => !Current.IsBusy && Current.CanProceed);
        BackCommand = new RelayCommand(() => Index--, () => Index > 0 && !Current.IsBusy);
        CancelCommand = new RelayCommand(() => ctx.CancelOperation(), () => Current.IsBusy);
        ToggleThemeCommand = new RelayCommand(ThemeService.Toggle);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        OpenLogsCommand = new RelayCommand(() => ctx.Dialogs.OpenPath(AppPaths.Logs));
        _ = Current.OnEnterAsync();
    }

    private void Refresh()
    {
        NextCommand.NotifyCanExecuteChanged(); BackCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(NextText));
    }

    private void CopyDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HackWizard {typeof(MainViewModel).Assembly.GetName().Version} — KB {Ctx.Kb.Manifest.KbVersion}");
        sb.AppendLine($"Step: {Current.Title}  Status: {Current.Status}  Error: {Current.Error}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        if (Ctx.Hardware is { } hw)
            sb.AppendLine(JsonSerializer.Serialize(new { hw.System.Manufacturer, hw.System.Model, hw.System.Version, hw.System.IsLaptop, hw.Cpu, hw.HostArchitecture, hw.TrackpadInterface,
                Devices = hw.Devices.Select(d => $"{d.Category} {d.PciIdKey} {d.Name}"), Disks = hw.Disks.Select(d => $"{d.Model} {d.BusType}") }, KnowledgeBase.JsonOptions));
        if (Ctx.Compat is { } c) sb.AppendLine($"SMBIOS {c.Smbios}; rules matched: " + string.Join(", ", c.MatchedRuleIds));
        if (Ctx.Build is { } b) sb.AppendLine("ocvalidate: " + b.Validation.Output);
        var log = Directory.GetFiles(AppPaths.Logs, "hackwizard-*.log").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
        if (log != null)
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            var lines = r.ReadToEnd().Split('\n');
            sb.AppendLine("--- last 200 log lines ---");
            foreach (var l in lines.Skip(Math.Max(0, lines.Length - 200))) sb.AppendLine(l.TrimEnd());
        }
        Clipboard.SetText(sb.ToString());
        Ctx.Dialogs.Info("Diagnostics", "Diagnostics copied to the clipboard. Serial numbers and MAC addresses are not included.");
    }
}
