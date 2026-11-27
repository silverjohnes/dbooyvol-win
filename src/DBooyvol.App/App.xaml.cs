using System.Windows;
using System.Windows.Threading;
using DBooyvol.App.Services;
using DBooyvol.App.ViewModels;
using DBooyvol.App.Views;
using DBooyvol.Core.Localization;
using DBooyvol.Core.Protocol;
using DBooyvol.Core.Shortcuts;
using DBooyvol.Core.Storage;

namespace DBooyvol.App;

public partial class App : Application
{
    private SidecarClient? _sidecar;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var settings = AppSettings.Load();
            L10n.Load(AppPaths.LocalesDirectory, settings.AppLanguage);

            var shortcuts = new ShortcutRegistry(settings.ShortcutOverrides);
            var hello = new HelloGate();
            var secrets = new SecretStore(AppPaths.Secrets, AppPaths.MasterKey,
                new WindowsSecretProtector(), hello.ConfirmAsync);

            var java = JavaLocator.Resolve(settings.JavaPath);
            Log.Info($"java: {java}");
            Log.Info($"sidecar: {AppPaths.SidecarJar}");

            _sidecar = new SidecarClient(new SidecarOptions(
                java,
                AppPaths.SidecarJar,
                AppPaths.AppDirectory,
                AppPaths.DriversDirectory));
            _sidecar.StderrReceived += message => Log.Info("sidecar: " + message);

            var viewModel = new MainViewModel(settings, shortcuts, secrets, _sidecar, hello);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
            viewModel.RestoreTabs();
        }
        catch (Exception error)
        {
            Log.Error("startup", error);
            MessageBox.Show(
                "Не удалось запустить DBooyvol.\n\n" + error.Message +
                "\n\nПодробности: " + AppPaths.Logs,
                "DBooyvol", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", e.Exception);
        MessageBox.Show(e.Exception.Message, "DBooyvol",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_sidecar is not null)
                _sidecar.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Shutdown must not fail because the JVM is already gone.
        }
        base.OnExit(e);
    }
}
