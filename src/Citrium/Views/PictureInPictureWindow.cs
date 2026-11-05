using System.Runtime.InteropServices;
using Citrium.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Citrium.Views;

/// <summary>
/// A small always-on-top live view of the session. It's a DWM thumbnail of the viewer window rather than the
/// session itself: shrinking the real surface would make Citrix renegotiate the remote desktop down to this
/// size and reflow everything on it, while a thumbnail scales the picture and leaves the session alone. A
/// thumbnail doesn't take input, so this view is watch-only and returning to the viewer is how you interact.
/// DWM draws it above this window's content, so nothing in XAML can sit on top of the picture.
/// </summary>
internal sealed class PictureInPictureWindow : Window
{
    private const int DefaultWidthDip = 480, MinWidthDip = 240, MinHeightDip = 180, ScreenMarginDip = 16, TitleBarDip = 48;

    private readonly IntPtr _source, _sessionArea;
    private readonly Grid _view;
    private IntPtr _thumbnail;
    private bool _closing;

    public event Action? ReturnRequested;

    public PictureInPictureWindow(string title, IntPtr source, IntPtr sessionArea, Windows.Graphics.RectInt32 workArea)
    {
        _source = source;
        _sessionArea = sessionArea;
        Title = title;

        var back = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Width = 36, Height = 32, Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(back, "Back to the desktop");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(back, "Back to the desktop");
        back.Click += (_, _) => ReturnRequested?.Invoke();

        var bar = new TitleBar { Title = title, RightHeader = back };

        _view = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        Grid.SetRow(_view, 1);
        ToolTipService.SetToolTip(_view, "Double-click to go back to the desktop");
        _view.DoubleTapped += (_, _) => ReturnRequested?.Invoke();
        _view.SizeChanged += (_, _) => Refresh();

        var root = new Grid
        {
            RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } },
            Children = { bar, _view },
        };
        Content = root;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(bar);
        WindowHelper.ApplyTheme(this, root);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Citrium.ico"));

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        presenter.PreferredMinimumWidth = (int)(MinWidthDip * scale);
        presenter.PreferredMinimumHeight = (int)(MinHeightDip * scale);
        AppWindow.SetPresenter(presenter);

        var area = SessionRect();
        var ratio = area.Right > area.Left ? (area.Bottom - area.Top) / (double)(area.Right - area.Left) : 9 / 16.0;
        var w = Math.Min((int)(DefaultWidthDip * scale), workArea.Width);
        var h = Math.Min((int)(w * ratio + TitleBarDip * scale), workArea.Height);
        var margin = (int)(ScreenMarginDip * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + workArea.Width - w - margin, workArea.Y + workArea.Height - h - margin, w, h));

        var hr = DwmRegisterThumbnail(hwnd, source, out _thumbnail);
        if (hr != 0) Log.Info($"Couldn't start the picture-in-picture view (0x{hr:X8})");

        AppWindow.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            ReturnRequested?.Invoke();
        };
        Closed += (_, _) => Unregister();
    }

    /// <summary>Closes the window for good, unlike the user closing it, which asks to return.</summary>
    public void Leave()
    {
        _closing = true;
        Unregister();
        Close();
    }

    public void Refresh()
    {
        if (_thumbnail == IntPtr.Zero || _view.XamlRoot is null) return;
        var scale = _view.XamlRoot.RasterizationScale;
        var origin = _view.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        int x = (int)Math.Round(origin.X * scale), y = (int)Math.Round(origin.Y * scale);
        int width = (int)Math.Round(_view.ActualWidth * scale), height = (int)Math.Round(_view.ActualHeight * scale);

        var source = SessionRect();
        int sourceWidth = source.Right - source.Left, sourceHeight = source.Bottom - source.Top;
        if (width <= 0 || height <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return;

        var fit = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
        int fittedWidth = (int)(sourceWidth * fit), fittedHeight = (int)(sourceHeight * fit);
        int left = x + (width - fittedWidth) / 2, top = y + (height - fittedHeight) / 2;

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT { Left = left, Top = top, Right = left + fittedWidth, Bottom = top + fittedHeight },
            rcSource = source,
            fVisible = 1,
            fSourceClientAreaOnly = 1,
        };
        var hr = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        if (hr != 0) Log.Info($"Couldn't update the picture-in-picture view (0x{hr:X8})");
    }

    private RECT SessionRect()
    {
        GetWindowRect(_sessionArea, out var area);
        var origin = new POINT();
        ClientToScreen(_source, ref origin);
        return new RECT
        {
            Left = area.Left - origin.X, Top = area.Top - origin.Y,
            Right = area.Right - origin.X, Bottom = area.Bottom - origin.Y,
        };
    }

    private void Unregister()
    {
        if (_thumbnail == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
    }

    private const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
