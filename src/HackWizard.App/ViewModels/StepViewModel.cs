using CommunityToolkit.Mvvm.ComponentModel;
using HackWizard.App.Services;
using Serilog;

namespace HackWizard.App.ViewModels;

public abstract class StepViewModel : ObservableObject
{
    protected StepViewModel(WizardContext ctx) => Ctx = ctx;
    protected WizardContext Ctx { get; }

    public abstract string Title { get; }
    public abstract string Explanation { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; protected set { if (SetProperty(ref _isBusy, value)) StateChanged?.Invoke(); } }
    private string _status = "";
    public string Status { get => _status; protected set => SetProperty(ref _status, value); }
    private double _progress;
    public double Progress { get => _progress; protected set => SetProperty(ref _progress, value); }
    private string _error = "";
    public string Error { get => _error; protected set { SetProperty(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);

    public abstract bool CanProceed { get; }
    public virtual Task OnEnterAsync() => Task.CompletedTask;
    public virtual void OnLeave() { }

    public event Action? StateChanged;
    protected void RaiseState() { OnPropertyChanged(nameof(CanProceed)); StateChanged?.Invoke(); }

    protected async Task RunGuarded(string what, Func<CancellationToken, Task> op)
    {
        if (IsBusy) return;
        Error = ""; IsBusy = true; Status = what + "…";
        var ct = Ctx.NewOperation();
        try { await op(ct); }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (HackWizard.Downloads.DownloadException ex) { Log.Error(ex, "{What} failed", what); Error = $"{ex.Message}\nSuggested fix: {ex.Fix}"; Status = "Failed."; }
        catch (Exception ex) { Log.Error(ex, "{What} failed", what); Error = $"{ex.Message}\n(See the log for details: Copy diagnostics.)"; Status = "Failed."; }
        finally { IsBusy = false; RaiseState(); }
    }
}
