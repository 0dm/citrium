using System.Runtime.InteropServices;
using Citrium.Models;
using Citrium.Services;
using Microsoft.UI.Dispatching;
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
/// Scrolling over it fades the window, and ghost mode lets clicks fall through to whatever is underneath.
/// </summary>
internal sealed class PictureInPictureWindow : Window
{
    private const int DefaultWidthDip = 480, MinWidthDip = 240, MinHeightDip = 180, ScreenMarginDip = 16, TitleBarDip = 48;
    private const double MinOpacity = 0.3, OpacityStep = 0.05, GhostOpacity = 0.7;
    private const string GhostHint = "Hold Alt to grab";

    private const double TitlePaddingDip = 16, CloseButtonSpaceDip = 52; // Windows' close button, and a small gap

    private readonly IntPtr _hwnd, _source, _sessionArea;
    private readonly Grid _view;
    private readonly TextBlock _hint;
    private readonly FontIcon _ghostIcon;
    private readonly DispatcherQueueTimer _ghostTimer, _hintTimer;
    private DwmThumbnail? _thumbnail;
    private double _opacity;
    private bool _ghost, _passingClicks, _closing;

    public event Action? ReturnRequested;
    public event Action<double>? OpacityChanged;

    public PictureInPictureWindow(string title, IntPtr source, IntPtr sessionArea, Windows.Graphics.RectInt32 workArea, double opacity)
    {
        _source = source;
        _sessionArea = sessionArea;
        _opacity = Math.Clamp(opacity, MinOpacity, 1);
        Title = title;

        var back = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Style = (Style)Application.Current.Resources["ViewerToolButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(back, "Back to the desktop");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(back, "Back to the desktop");
        back.Click += (_, _) => ReturnRequested?.Invoke();

        _ghostIcon = new FontIcon { Glyph = "", FontSize = 14 };
        var ghost = new Button
        {
            Content = _ghostIcon,
            Style = (Style)Application.Current.Resources["ViewerToolButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(ghost, "Ghost mode: clicks pass through this window. Hold Alt to grab it again.");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ghost, "Ghost mode");
        ghost.Click += (_, _) => SetGhost(!_ghost);

        // Laid out by hand rather than with the TitleBar control, which keeps a gap for buttons this window doesn't
        // have and treats anything moved into that gap as title bar, so it would drag instead of click.
        _hint = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleArea = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // transparent, not null: the drag needs a surface
            Padding = new Thickness(TitlePaddingDip, 0, 0, 0),
            ColumnSpacing = 8,
            ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Auto) }, new() { Width = new GridLength(1, GridUnitType.Star) } },
        };
        var name = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(_hint, 1);
        titleArea.Children.Add(name);
        titleArea.Children.Add(_hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, CloseButtonSpaceDip, 0),
            Children = { ghost, back },
        };
        Grid.SetColumn(buttons, 1);

        var bar = new Grid
        {
            Height = TitleBarDip,
            ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
            Children = { titleArea, buttons },
        };

        _view = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        Grid.SetRow(_view, 1);
        ToolTipService.SetToolTip(_view, "Double-click to go back to the desktop. Scroll to fade.");
        _view.DoubleTapped += (_, _) => ReturnRequested?.Invoke();
        _view.SizeChanged += (_, _) => Refresh();
        _view.PointerWheelChanged += OnWheel;

        var root = new Grid
        {
            RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } },
            Children = { bar, _view },
        };
        Content = root;

        // Otherwise focus lands on the first button with its ring showing. On Back, Enter returns to the desktop.
        root.Loaded += (_, _) => back.Focus(FocusState.Pointer);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleArea);

        // Tall, like Citrium's other windows: otherwise Windows' close button stays short and sits above the row.
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        WindowHelper.ApplyTheme(this, root);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Citrium.ico"));

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        presenter.PreferredMinimumWidth = (int)(MinWidthDip * scale);
        presenter.PreferredMinimumHeight = (int)(MinHeightDip * scale);
        AppWindow.SetPresenter(presenter);

        WindowHelper.MoveTo(this, SavedPlace() ?? DefaultPlace(workArea, scale));
        ApplyLook();

        _thumbnail = DwmThumbnail.TryCreate(_hwnd, source);

        _ghostTimer = DispatcherQueue.CreateTimer();
        _ghostTimer.Interval = TimeSpan.FromMilliseconds(100);
        _ghostTimer.Tick += (_, _) => FollowAltKey();

        _hintTimer = DispatcherQueue.CreateTimer();
        _hintTimer.Interval = TimeSpan.FromSeconds(1.2);
        _hintTimer.IsRepeating = false;
        _hintTimer.Tick += (_, _) => _hint.Text = _ghost ? GhostHint : "";

        AppWindow.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            ReturnRequested?.Invoke();
        };
        Closed += (_, _) => Unregister();
    }

    private static ScreenRect? SavedPlace() =>
        PlacementStore.Load().PictureInPicture is { } p
            ? WindowFit.Fit(new ScreenRect(p.X, p.Y, p.Width, p.Height), WindowHelper.WorkAreas())
            : null;

    private ScreenRect DefaultPlace(Windows.Graphics.RectInt32 workArea, double scale)
    {
        var area = DwmThumbnail.ChildRect(_source, _sessionArea);
        var ratio = area.Width > 0 ? area.Height / (double)area.Width : 9 / 16.0;
        var w = Math.Min((int)(DefaultWidthDip * scale), workArea.Width);
        var h = Math.Min((int)(w * ratio + TitleBarDip * scale), workArea.Height);
        var margin = (int)(ScreenMarginDip * scale);
        return new ScreenRect(workArea.X + workArea.Width - w - margin, workArea.Y + workArea.Height - h - margin, w, h);
    }

    /// <summary>Closes the window for good, unlike the user closing it, which asks to return.</summary>
    public void Leave()
    {
        _closing = true;
        _ghostTimer.Stop();
        _hintTimer.Stop();
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        PlacementStore.Update(p => p.PictureInPicture = new WindowPlacement
        {
            X = position.X, Y = position.Y, Width = size.Width, Height = size.Height,
        });
        Unregister();
        Close();
    }

    public void Refresh()
    {
        if (_thumbnail is null || _view.XamlRoot is null) return;
        var scale = _view.XamlRoot.RasterizationScale;
        var origin = _view.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        int x = (int)Math.Round(origin.X * scale), y = (int)Math.Round(origin.Y * scale);
        int width = (int)Math.Round(_view.ActualWidth * scale), height = (int)Math.Round(_view.ActualHeight * scale);

        var source = DwmThumbnail.ChildRect(_source, _sessionArea);
        if (width <= 0 || height <= 0 || source.Width <= 0 || source.Height <= 0) return;
        var box = new NativeRect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        _thumbnail.Show(DwmThumbnail.Letterbox(source, box), source);
    }

    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var notches = e.GetCurrentPoint(_view).Properties.MouseWheelDelta / 120.0;
        _opacity = Math.Clamp(Math.Round((_opacity + notches * OpacityStep) / OpacityStep) * OpacityStep, MinOpacity, 1);
        ApplyLook();
        OpacityChanged?.Invoke(_opacity);
        _hint.Text = $"{_opacity:P0}";
        _hintTimer.Stop();
        _hintTimer.Start();
        e.Handled = true;
    }

    private void SetGhost(bool on)
    {
        _ghost = on;
        _hint.Text = on ? GhostHint : "";

        // The accent stays on while Alt is held: that lifts ghost mode for a moment, it doesn't turn it off.
        if (on) _ghostIcon.Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        else _ghostIcon.ClearValue(IconElement.ForegroundProperty);

        if (on) _ghostTimer.Start();
        else _ghostTimer.Stop();
        _passingClicks = on;
        ApplyLook();
    }

    /// <summary>A window that ignores clicks can't be clicked back to normal, so Alt lifts ghost mode while it's held.</summary>
    private void FollowAltKey()
    {
        var pass = (GetAsyncKeyState(VK_MENU) & 0x8000) == 0;
        if (pass == _passingClicks) return;
        _passingClicks = pass;
        ApplyLook();
    }

    /// <summary>A fully solid window isn't layered at all, so anyone who never fades it gets exactly the plain window.</summary>
    private void ApplyLook()
    {
        var opacity = _ghost ? Math.Min(_opacity, GhostOpacity) : _opacity;
        var style = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE).ToInt64();
        var layered = opacity < 1 ? style | WS_EX_LAYERED : style & ~WS_EX_LAYERED;
        var next = _passingClicks ? layered | WS_EX_TRANSPARENT : layered & ~WS_EX_TRANSPARENT;
        if (next != style) SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, new IntPtr(next));
        if (opacity < 1) SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round(opacity * 255), LWA_ALPHA);
    }

    private void Unregister()
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
    }

    private const int GWL_EXSTYLE = -20, VK_MENU = 0x12;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 2;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
}
