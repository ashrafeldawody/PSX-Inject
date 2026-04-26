using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace PsxInject.Services;

/// <summary>
/// Owns the system-tray icon. The icon is rendered programmatically and persisted
/// as a Vista-style PNG-inside-ICO file in %TEMP% so H.NotifyIcon can load it via
/// a normal URI (the only ImageSource path it supports without external assets).
/// </summary>
public class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private MenuItem? _toggleItem;

    private static readonly string IconRunningPath =
        Path.Combine(Path.GetTempPath(), "PsxInject-tray-on.ico");

    private static readonly string IconStoppedPath =
        Path.Combine(Path.GetTempPath(), "PsxInject-tray-off.ico");

    public event Action? ShowRequested;
    public event Action? ToggleServerRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        EnsureIconFile(IconRunningPath, running: true);
        EnsureIconFile(IconStoppedPath, running: false);

        _icon = new TaskbarIcon
        {
            ToolTipText = "PSX inject",
            IconSource = LoadIcon(IconStoppedPath),
            Visibility = Visibility.Collapsed,
            NoLeftClickDelay = true
        };

        _icon.TrayMouseDoubleClick += (_, _) => ShowRequested?.Invoke();
        _icon.LeftClickCommand = new RelayCommand(() => ShowRequested?.Invoke());

        _icon.ContextMenu = BuildContextMenu();
    }

    public void Show() => _icon.Visibility = Visibility.Visible;
    public void Hide() => _icon.Visibility = Visibility.Collapsed;

    public void SetServerRunning(bool running)
    {
        _icon.IconSource = LoadIcon(running ? IconRunningPath : IconStoppedPath);
        _icon.ToolTipText = running
            ? "PSX inject — running"
            : "PSX inject — stopped";

        if (_toggleItem is not null)
            _toggleItem.Header = running ? "Stop server" : "Start server";
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Open PSX inject" };
        show.Click += (_, _) => ShowRequested?.Invoke();

        _toggleItem = new MenuItem { Header = "Start server" };
        _toggleItem.Click += (_, _) => ToggleServerRequested?.Invoke();

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(show);
        menu.Items.Add(new Separator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        return menu;
    }

    private static BitmapImage LoadIcon(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        // Force WPF to actually re-read the file rather than reuse a cached decode.
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void EnsureIconFile(string path, bool running)
    {
        try
        {
            var rtb = RenderBitmap(running);
            var icoBytes = BuildIco(rtb);
            File.WriteAllBytes(path, icoBytes);
        }
        catch
        {
            // Best-effort. If we can't write the icon, the tray will show a default glyph.
        }
    }

    private static RenderTargetBitmap RenderBitmap(bool running)
    {
        const int size = 32;
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var bg = new LinearGradientBrush(
                Color.FromRgb(0x1E, 0x90, 0xFF),
                Color.FromRgb(0x00, 0x70, 0xD1),
                45);
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), 7, 7);

            var ft = new FormattedText(
                "PS",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"),
                             FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                15,
                Brushes.White,
                1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2 - 1));

            if (running)
            {
                dc.DrawEllipse(
                    new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
                    new Pen(Brushes.White, 1.2),
                    new Point(size - 7, 7),
                    4.5, 4.5);
            }
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    /// <summary>
    /// Builds a Vista+ icon file: ICONDIR + 1× ICONDIRENTRY + a single PNG payload.
    /// </summary>
    private static byte[] BuildIco(BitmapSource bmp)
    {
        var pngBytes = EncodePng(bmp);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ICONDIR
        w.Write((ushort)0);   // reserved
        w.Write((ushort)1);   // type: 1 = icon
        w.Write((ushort)1);   // image count

        // ICONDIRENTRY
        w.Write((byte)(bmp.PixelWidth  >= 256 ? 0 : bmp.PixelWidth));
        w.Write((byte)(bmp.PixelHeight >= 256 ? 0 : bmp.PixelHeight));
        w.Write((byte)0);     // colors in palette (0 = none)
        w.Write((byte)0);     // reserved
        w.Write((ushort)1);   // color planes
        w.Write((ushort)32);  // bits per pixel
        w.Write((uint)pngBytes.Length); // image data size
        w.Write((uint)22);    // offset (6 + 16)

        w.Write(pngBytes);

        return ms.ToArray();
    }

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        try { _icon.Dispose(); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged
        {
            add { } remove { }
        }
    }
}
