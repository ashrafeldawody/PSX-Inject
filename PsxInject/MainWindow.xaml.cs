using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PsxInject.Models;
using PsxInject.ViewModels;
using PsxInject.Views;

namespace PsxInject;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private bool _terminating;
    private bool _trackingRunningState;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            int caption = 0x00131825;
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

            int border = 0x001F2740;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch { /* older Windows */ }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        // Forward server running-state changes to the tray icon.
        if (!_trackingRunningState)
        {
            _trackingRunningState = true;
            Vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsRunning))
                    App.Current?.NotifyServerRunning(Vm.IsRunning);
            };
        }

        await Vm.InitializeAsync();
        App.Current?.NotifyServerRunning(Vm.IsRunning);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Tray is shown only while the window is hidden.
        if (App.Current is { } app)
        {
            if (IsVisible) app.HideTray();
            else app.ShowTray();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_terminating) { base.OnClosing(e); return; }
        if (Vm is null) { base.OnClosing(e); return; }

        // Decide what "close" means based on saved preference and current state.
        var action = Vm.CloseAction;

        if (!Vm.IsRunning && action == CloseAction.Ask)
        {
            // Nothing meaningful happening — just exit silently.
            TerminateNow(e);
            return;
        }

        if (action == CloseAction.Exit)
        {
            TerminateNow(e);
            return;
        }

        if (action == CloseAction.RunInBackground)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // CloseAction.Ask → show dialog.
        var dialog = new CloseConfirmDialog { Owner = this };
        dialog.ShowDialog();

        switch (dialog.Choice)
        {
            case CloseChoice.Cancel:
                e.Cancel = true;
                return;

            case CloseChoice.RunInBackground:
                if (dialog.RememberChoice) Vm.PersistCloseAction(CloseAction.RunInBackground);
                e.Cancel = true;
                HideToTray();
                return;

            case CloseChoice.StopAndExit:
                if (dialog.RememberChoice) Vm.PersistCloseAction(CloseAction.Exit);
                TerminateNow(e);
                return;
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public async void ToggleServerFromTray()
    {
        if (Vm is null) return;
        if (Vm.IsRunning) await Vm.StopCommand.ExecuteAsync(null);
        else await Vm.StartCommand.ExecuteAsync(null);
    }

    public async void ExitFromTray()
    {
        await TerminateAsync();
    }

    private void TerminateNow(CancelEventArgs e)
    {
        // Cancel this close — we'll do the cleanup async, then exit ourselves.
        e.Cancel = true;
        _ = TerminateAsync();
    }

    private async Task TerminateAsync()
    {
        if (_terminating) return;
        _terminating = true;

        // Hide immediately so the user gets feedback while the listener winds down.
        try { Hide(); } catch { /* window may already be closing */ }

        // Stop the server on a thread-pool thread to keep the UI responsive
        // and avoid sync-over-async deadlocks on the dispatcher.
        try
        {
            await Task.Run(() => Vm?.Dispose());
        }
        catch { /* ignore */ }

        App.Current?.TerminateApplication();
    }
}
