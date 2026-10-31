using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;
using HackWizard.Core.Usb;
using Serilog;

namespace HackWizard.App.ViewModels;

/// <summary>Built-in USB port mapping (replaces running USBToolBox's Windows.exe). Live port discovery via Windows USB
/// IOCTLs; the selection becomes UTBMap.kext at build time. With no selection, UTBDefault.kext is used instead.</summary>
public sealed class UsbMapViewModel : StepViewModel
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private UsbMapState? _state;
    private bool _scanning;

    public UsbMapViewModel(WizardContext ctx) : base(ctx)
    {
        _timer.Tick += async (_, _) => await TickAsync();
        ToggleDiscoveryCommand = new RelayCommand(() => Discovering = !Discovering);
        AutoSelectCommand = new RelayCommand(() => { _state?.AutoSelect(); OnChanged(); });
        ClearCommand = new RelayCommand(() => { _state?.ClearSelection(); OnChanged(); });
        ResetCommand = new AsyncRelayCommand(() => LoadAsync(forget: true));
    }

    public override string Title => "5. USB port map";
    public override string Explanation =>
        "macOS only enables 15 USB ports per controller, so it needs a map of the ports you actually use. Map them now, while Windows is still installed " +
        "(Windows reports which USB 2 and USB 3 ports belong to the same socket, which macOS cannot). You can also skip this and map later.";

    public string Instructions =>
        "1. Leave discovery running.\n" +
        "2. Plug a USB 2 device (mouse, keyboard or its receiver) into every USB port one at a time, until the port shows '● connected'.\n" +
        "3. Do the same with a USB 3 stick. For USB-C ports, plug in both ways round (flip the plug).\n" +
        "4. Built-in devices (webcam, Bluetooth, fingerprint reader) are found automatically.\n" +
        "5. Check the Connector column, untick anything you don't need (e.g. fingerprint reader), and keep each controller at 15 or fewer.";

    public ObservableCollection<UsbController> Controllers { get; } = new();
    public RelayCommand ToggleDiscoveryCommand { get; }
    public RelayCommand AutoSelectCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }

    private bool _discovering;
    public bool Discovering
    {
        get => _discovering;
        set
        {
            if (!SetProperty(ref _discovering, value)) return;
            if (value) _timer.Start(); else _timer.Stop();
            OnPropertyChanged(nameof(DiscoverButtonText));
        }
    }
    public string DiscoverButtonText => Discovering ? "Pause discovery" : "Start discovery";

    public string Summary
    {
        get
        {
            if (_state is null) return "";
            var over = _state.Controllers.Where(c => c.IsXhci && c.SelectedCount > UsbMapState.MaxPorts).ToList();
            if (over.Count > 0) return "Too many ports selected on: " + string.Join(", ", over.Select(c => c.Name)) + ". Untick ports until each controller has 15 or fewer.";
            if (_state.SelectedCount == 0)
                return "No ports selected: mapping is skipped. The USB drive will use UTBDefault.kext (all ports enabled, up to 15 per controller) and you can map later.";
            var dup = _state.Mapped.GroupBy(c => c.PrimaryMatch).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            return $"{_state.SelectedCount} ports on {_state.Mapped.Count()} controller(s) will be written to UTBMap.kext."
                   + (dup.Count > 0 ? $"  ⚠ Controllers with identical IDs ({string.Join(", ", dup)}) share one map; map them identically." : "");
        }
    }

    public override bool CanProceed => _state is { OverLimit: false };

    public override Task OnEnterAsync() => _state is null ? LoadAsync(forget: false) : StartAsync();

    public override void OnLeave()
    {
        Discovering = false;
        _state?.Save(UsbMapState.DefaultPath);
    }

    private Task StartAsync() { Discovering = true; return Task.CompletedTask; }

    private Task LoadAsync(bool forget) => RunGuarded("Reading USB controllers", async ct =>
    {
        Discovering = false;
        var fresh = await Task.Run(UsbScanner.ListControllers, ct);
        var st = forget ? new UsbMapState() : UsbMapState.Load(UsbMapState.DefaultPath) ?? new UsbMapState();
        st.MergeTopology(fresh);
        _state = st;
        Ctx.UsbMap = st;
        Controllers.Clear();
        foreach (var c in st.Controllers) { c.Attach(); c.Changed += OnChanged; Controllers.Add(c); }
        OnChanged();
        Status = $"{st.Controllers.Count(c => c.IsXhci)} USB 3 controller(s), {st.Controllers.Sum(c => c.Ports.Count)} ports. Discovery is running — plug devices in now.";
        Discovering = true;
    });

    private void OnChanged()
    {
        if (_state is null) return;
        Ctx.Build = null;
        _state.Save(UsbMapState.DefaultPath);
        OnPropertyChanged(nameof(Summary));
        RaiseState();
    }

    private async Task TickAsync()
    {
        if (_scanning || _state is null || IsBusy) return;
        _scanning = true;
        try
        {
            var controllers = _state.Controllers.Where(c => c.IsXhci && c.Ports.Count > 0).ToList();
            var snapshots = await Task.Run(() => controllers.Select(c => (c, live: UsbScanner.ReadLive(c))).ToList());
            var changed = false;
            foreach (var (c, live) in snapshots) changed |= _state.ApplySnapshot(c, live, autoSelect: true);
            if (changed) OnChanged();
        }
        catch (Exception ex) { Log.Warning(ex, "USB live scan failed"); }
        finally { _scanning = false; }
    }
}
