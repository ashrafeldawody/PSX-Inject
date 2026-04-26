using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PsxInject.Views;

public enum CloseChoice
{
    Cancel,
    StopAndExit,
    RunInBackground
}

public partial class CloseConfirmDialog : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public CloseChoice Choice { get; private set; } = CloseChoice.Cancel;
    public bool RememberChoice => DontAskAgain.IsChecked == true;

    public CloseConfirmDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }
        };
    }

    private void OnStopAndExit(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.StopAndExit;
        DialogResult = true;
        Close();
    }

    private void OnRunInBackground(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.RunInBackground;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
