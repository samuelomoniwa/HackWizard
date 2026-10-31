using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using HackWizard.App.Services;
using HackWizard.App.ViewModels;
using HackWizard.Core.Logging;
using HackWizard.Core.Rules;
using Serilog;

namespace HackWizard.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(System.IO.Path.Combine(AppPaths.Logs, "hackwizard-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("HackWizard {Version} starting on {OS}; elevated={Admin}", typeof(App).Assembly.GetName().Version, Environment.OSVersion,
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Fatal(a.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(a.Exception, "Unobserved task exception"); a.SetObserved(); };

        ThemeService.ApplySystemTheme();
        KnowledgeBase kb;
        try { kb = KnowledgeBaseLoader.LoadOrSeed(); }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Knowledge base failed to load");
            MessageBox.Show("The rules knowledge base could not be loaded:\n" + ex.Message, "HackWizard", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        var ctx = new WizardContext { Kb = kb, Dialogs = new DialogService() };
        var win = new MainWindow { DataContext = new MainViewModel(ctx) };
        MainWindow = win;
        win.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI exception");
        MessageBox.Show(e.Exception.Message + "\n\nDetails were written to the log (use Copy diagnostics).", "HackWizard error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("HackWizard exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
