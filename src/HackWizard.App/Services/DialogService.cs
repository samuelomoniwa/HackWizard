using System.Diagnostics;
using System.Windows;

namespace HackWizard.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message, bool dangerous = false);
    void Info(string title, string message);
    void Error(string title, string message);
    void OpenPath(string pathOrUrl);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message, bool dangerous = false) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo,
            dangerous ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    public void Info(string title, string message) => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public void Error(string title, string message) => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    public void OpenPath(string pathOrUrl) => Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
}
