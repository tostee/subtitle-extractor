using System.Windows;
using System.Windows.Threading;
using SubtitleTool.App.Services;
using SubtitleTool.App.ViewModels;
using SubtitleTool.App.Views;

namespace SubtitleTool.App;

public partial class App : Application
{
    private MainViewModel? _main;
    private readonly FileActivityLog _activityLog = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var activityLog = _activityLog;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Dispatcher.BeginInvoke(() =>
            {
                if (_main is not null)
                    _main.LogException("Error en segundo plano", args.Exception.GetBaseException());
                else
                    activityLog.WriteException("Error en segundo plano", args.Exception);
            });
        };
        // Último recurso: si la app se cierra por un error no controlado, al menos queda la traza en el registro.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                activityLog.WriteException("Error fatal", ex);
        };
        activityLog.Write($"Inicio de Subtítulos {typeof(App).Assembly.GetName().Version} en {Environment.OSVersion}");

        var dialogs = new DialogService();
        _main = new MainViewModel(new JsonSettingsStore(), new LibraryService(), new VideoProcessingService(),
            new GlossaryService(), new DoctorServiceAdapter(), new ApiKeyService(), dialogs, activityLog, new GitHubUpdateService());
        var window = new MainWindow { DataContext = _main };
        dialogs.Owner = window;
        MainWindow = window;
        window.Show();
        _main.ReportToolStatus();
        _ = _main.CheckForUpdatesOnStartupAsync();

        // Subtitulos.exe "<carpeta o video>" …: se agregan al iniciar.
        if (e.Args.Length > 0)
            _ = _main.AddPathsAsync(e.Args);
    }

    /// <summary>Un error inesperado nunca cierra la app: se registra y se muestra.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_main is not null)
            _main.LogException("Error inesperado", e.Exception);
        else
            _activityLog.WriteException("Error inesperado", e.Exception);
        MessageBox.Show(MainWindow!, e.Exception.Message, "Error inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
