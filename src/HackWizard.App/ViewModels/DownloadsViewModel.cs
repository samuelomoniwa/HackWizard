using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HackWizard.App.Services;

namespace HackWizard.App.ViewModels;

public sealed class DownloadItem : ObservableObject
{
    public string Id { get; init; } = "";
    private string _status = "Waiting"; public string Status { get => _status; set => SetProperty(ref _status, value); }
    private double _percent; public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    private string _detail = ""; public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
}

public sealed class DownloadsViewModel : StepViewModel
{
    public DownloadsViewModel(WizardContext ctx) : base(ctx) => StartCommand = new AsyncRelayCommand(StartAsync);
    public override string Title => "6. Downloads";
    public override string Explanation =>
        "Downloads the latest stable OpenCore, the drivers (kexts) your hardware needs and macOS Recovery, only from their official sources. " +
        "Every file is hash-checked and logged. Large files resume if interrupted. Nothing is written to any disk except HackWizard's cache.";
    public ObservableCollection<DownloadItem> Items { get; } = new();
    public AsyncRelayCommand StartCommand { get; }
    private bool _offline; public bool Offline { get => _offline; set => SetProperty(ref _offline, value); }
    public override bool CanProceed => Ctx.Downloads != null;

    private Task StartAsync() => RunGuarded("Downloading", async ct =>
    {
        Items.Clear(); Ctx.Downloads = null; Ctx.Build = null;
        DownloadItem Item(string id) { var i = Items.FirstOrDefault(x => x.Id == id); if (i is null) Items.Add(i = new DownloadItem { Id = id }); return i; }
        Ctx.Downloads = await EfiPipeline.DownloadAllAsync(Ctx, Offline, (id, p) =>
        {
            var it = Item(id); it.Status = p.Status;
            if (p.Total is > 0) it.Percent = p.Percent; else if (p.Status.StartsWith("Cached") || p.Status.StartsWith("Verified")) it.Percent = 100;
            it.Detail = p.Total is > 0 ? $"{p.Received / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB" : "";
        }, ct);
        foreach (var a in Ctx.Downloads.Manager.Log) { var it = Item(a.SourceId); it.Percent = 100; it.Status = $"{a.Version} ✓"; it.Detail = $"SHA-256 {a.Sha256[..16]}… ({a.HashSource})"; }
        if (Ctx.Downloads.Recovery is { } r) { var it = Item("recovery"); it.Percent = 100; it.Status = "macOS Recovery ✓"; it.Detail = $"{Path.GetFileName(r.DmgPath)} — {r.ChunksVerified} chunks verified"; }
        Status = "All downloads complete and verified.";
    });
}
