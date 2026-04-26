using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PsxInject.Services;

namespace PsxInject;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private const string MutexName = "Global\\PsxInject.SingleInstance.{8A3F1A21-7E5B-4F8C-9A23-1234567890AB}";

    public TrayIconService? Tray { get; private set; }

    public new static App? Current => Application.Current as App;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "PSX inject is already running.\n\nCheck your taskbar or system tray.",
                "Already running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Keep the process alive when all windows are hidden (e.g. running in tray).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Tray = new TrayIconService();
        Tray.ShowRequested += () => MainWindowInstance?.RestoreFromTray();
        Tray.ToggleServerRequested += () => MainWindowInstance?.ToggleServerFromTray();
        Tray.ExitRequested += () => MainWindowInstance?.ExitFromTray();

        base.OnStartup(e);
    }

    private MainWindow? MainWindowInstance => MainWindow as MainWindow;

    public void ShowTray() => Tray?.Show();
    public void HideTray() => Tray?.Hide();
    public void NotifyServerRunning(bool running) => Tray?.SetServerRunning(running);

    public void TerminateApplication()
    {
        Tray?.Hide();
        Tray?.Dispose();
        Tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        Tray = null;
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe app will continue running.",
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            System.Diagnostics.Debug.WriteLine($"[UnhandledException] {ex}");
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[UnobservedTaskException] {e.Exception}");
        e.SetObserved();
    }
}
