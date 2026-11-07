using System.Runtime.InteropServices;
using Citrium.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Citrium.Views;

internal enum ToolbarDock { Top, Bottom, Left, Right, Floating }

internal readonly record struct PanelAnchor(int X, int Y, bool AlignRight, bool AlignBottom);

/// <summary>
/// The bar shown over a full-screen session: a small always-on-top window, because nothing drawn by XAML can appear
/// above the native session surface. Dragged by its grip it floats anywhere, and docks to an edge when brought near
/// one, turning vertical on the left and right. A standard command bar can't run vertically, so that orientation
/// has its own compact column of the same commands.
/// </summary>
internal sealed class FullScreenToolbar : Window
{
    private const int ThicknessDip = 56;
    private const int EdgeMarginDip = 6;
    private const int DockDistanceDip = 48;
    private const int UndockDistanceDip = 72; // further than docking, so the bar doesn't flicker at the boundary
    private const int CenterSnapDip = 40;
    private const int PanelGapDip = 6;
    private const string PinGlyph = "", PinnedGlyph = "";

    private readonly StackPanel _root;
    private readonly Grip _grip;
    private readonly FontIcon _gripIcon;
    private readonly StackPanel _gripContent;
    private readonly TextBlock _name;
    private readonly CommandBar _bar;
    private readonly StackPanel _column;
    private readonly AppBarToggleButton _pin, _columnPin;

    private ToolbarDock _dock;
    private double _centerX, _centerY; // the bar's middle, as a fraction of the monitor
    private Windows.Graphics.RectInt32 _monitor;
    private bool _pinned;
    private Windows.Foundation.Size _rowSize, _columnSize; // in dips, once that orientation has been laid out
    private int _minimumWidth; // pixels; even with the hook below, a bordered window can't be narrower
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _dragTimer;
    private bool _dragging, _moved;
    private (int X, int Y) _grab, _lastCursor;

    public event Action? CtrlAltDelRequested;
    public event Action? MinimizeRequested;
    public event Action? NextScreenRequested;
    public event Action? ExitFullScreenRequested;
    public event Action? PictureInPictureRequested;
    public event Action? SendToCitriumRequested;
    public event Action? DisconnectRequested;
    public event Action<bool>? PinChanged;
    public event Action? SettingsRequested;

    public event Action<ToolbarDock, double, double>? PlacementChanged;

    public bool IsShown => AppWindow.IsVisible;
    public bool IsPinned => _pinned;
    public bool IsDragging => _dragging;
    public ToolbarDock Dock => _dock;
    private bool IsVertical => _dock is ToolbarDock.Left or ToolbarDock.Right;

    public FullScreenToolbar(string title, bool pinned, ToolbarDock dock, double centerX, double centerY, bool multipleScreens)
    {
        _pinned = pinned;
        _dock = dock;
        _centerX = centerX;
        _centerY = centerY;

        _bar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            IsDynamicOverflowEnabled = false,
        };
        _column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2, Padding = new Thickness(0, 0, 0, 6) };

        AddCommand("", "Ctrl+Alt+Del", "Send Ctrl+Alt+Del", () => CtrlAltDelRequested?.Invoke());
        AddSeparator();
        AddCommand("", "Minimize", "Minimize", () => MinimizeRequested?.Invoke());
        AddCommand("", "Exit full screen", "Exit full screen", () => ExitFullScreenRequested?.Invoke());
        if (multipleScreens) AddCommand("\uE7F4", "Next screen", "Move this desktop to your next screen", () => NextScreenRequested?.Invoke());
        AddCommand("\uE944", "Picture in picture", "Picture in picture", () => PictureInPictureRequested?.Invoke());
        AddCommand("\uE724", "Send to Citrium", "Keeps running. Reopen it from Sessions in Citrium.", () => SendToCitriumRequested?.Invoke());
        AddCommand("", "Disconnect", "Disconnect (the desktop keeps running)", () => DisconnectRequested?.Invoke());
        AddSeparator();
        AddCommand("", "Settings", "Settings", () => SettingsRequested?.Invoke());

        _pin = new AppBarToggleButton { Label = "Pin" };
        _columnPin = Compact(new AppBarToggleButton { Label = "Pin" });
        foreach (var pin in new[] { _pin, _columnPin })
        {
            pin.Icon = new FontIcon { Glyph = PinGlyph };
            ToolTipService.SetToolTip(pin, "Keep the toolbar visible");
            var button = pin;
            button.Click += (_, _) => PinChanged?.Invoke(button.IsChecked == true);
        }
        _bar.PrimaryCommands.Add(_pin);
        _column.Children.Add(_columnPin);
        SetPinned(pinned);

        _gripIcon = new FontIcon { FontSize = 14, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _name = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 240,
            Margin = new Thickness(8, 0, 8, 0),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };

        _gripContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _gripContent.Children.Add(_gripIcon);
        _gripContent.Children.Add(_name);
        _grip = new Grip();
        _grip.Children.Add(_gripContent);
        ToolTipService.SetToolTip(_grip, "Drag to move. Bring it to a screen edge to dock it.");
        _grip.PointerPressed += OnGripPressed;
        _grip.PointerReleased += OnGripReleased;
        _grip.PointerCaptureLost += (_, _) => EndDrag();

        // The drag follows the cursor on a timer rather than on pointer events: moving a window per event falls
        // behind the pointer, while this always goes straight to where the pointer is now.
        _dragTimer = DispatcherQueue.CreateTimer();
        _dragTimer.Interval = TimeSpan.FromMilliseconds(8);
        _dragTimer.Tick += (_, _) => FollowCursor();

        _root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _root.Children.Add(_grip);
        _root.Children.Add(_bar);
        _root.Children.Add(_column);
        Content = _root;
        ApplyOrientation();

        WindowHelper.ApplyTheme(this, _root);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false); // the border is what gets Windows 11's own corners and edge
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        WindowHelper.SetShownInSwitchers(this, false);

        // Windows keeps every window as wide as a title bar needs, more than twice the vertical bar's width.
        SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), MinimumSizeHook, 1, 0);
    }

    private static readonly SubclassProc MinimumSizeHook = (hwnd, message, wParam, lParam, id, data) =>
    {
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == WM_GETMINMAXINFO)
        {
            Marshal.WriteInt32(lParam, MinTrackSizeOffset, 1);
            Marshal.WriteInt32(lParam, MinTrackSizeOffset + sizeof(int), 1);
        }
        return result;
    };

    private void AddCommand(string glyph, string label, string tooltip, Action onClick)
    {
        foreach (var button in new[] { new AppBarButton { Label = label }, Compact(new AppBarButton { Label = label }) })
        {
            button.Icon = new FontIcon { Glyph = glyph };
            ToolTipService.SetToolTip(button, tooltip);
            button.Click += (_, _) => onClick();
            if (button.IsCompact) _column.Children.Add(button);
            else _bar.PrimaryCommands.Add(button);
        }
    }

    private void AddSeparator()
    {
        _bar.PrimaryCommands.Add(new AppBarSeparator());
        _column.Children.Add(new Rectangle
        {
            Height = 1,
            Margin = new Thickness(10, 3, 10, 3),
            Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        });
    }

    private static T Compact<T>(T button) where T : FrameworkElement, ICommandBarElement
    {
        button.IsCompact = true;
        button.Width = 44;
        button.Height = 44;
        return button;
    }

    public void SetPinned(bool pinned)
    {
        _pinned = pinned;
        foreach (var pin in new[] { _pin, _columnPin })
        {
            pin.IsChecked = pinned;
            ((FontIcon)pin.Icon).Glyph = pinned ? PinnedGlyph : PinGlyph;
        }
    }

    public void ShowOn(Windows.Graphics.RectInt32 monitor)
    {
        _monitor = monitor;
        Place();
        AppWindow.Show(false); // don't steal keyboard focus from the session
        WatchFirstLayout();
    }

    public bool ContainsScreenPoint(int x, int y, int margin)
    {
        var p = AppWindow.Position;
        var s = AppWindow.Size;
        return x >= p.X - margin && x < p.X + s.Width + margin && y >= p.Y - margin - 8 && y < p.Y + s.Height + margin;
    }

    public PanelAnchor SettingsPanelAnchor()
    {
        var p = AppWindow.Position;
        var s = AppWindow.Size;
        var gap = (int)(PanelGapDip * Scale);
        return _dock switch
        {
            ToolbarDock.Left => new PanelAnchor(p.X + s.Width + gap, p.Y, false, false),
            ToolbarDock.Right => new PanelAnchor(p.X - gap, p.Y, true, false),
            ToolbarDock.Bottom => new PanelAnchor(p.X + s.Width, p.Y - gap, true, true),
            ToolbarDock.Floating when _centerY > 0.5 => new PanelAnchor(p.X + s.Width, p.Y - gap, true, true),
            _ => new PanelAnchor(p.X + s.Width, p.Y + s.Height + gap, true, false),
        };
    }

    private void OnGripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed || !GetCursorPos(out var cursor)) return;
        var p = AppWindow.Position;
        _grab = (cursor.X - p.X, cursor.Y - p.Y);
        _lastCursor = (cursor.X, cursor.Y);
        _dragging = true;
        _moved = false;
        _grip.CapturePointer(e.Pointer);
        _dragTimer.Start();
        e.Handled = true;
    }

    private void FollowCursor()
    {
        if (!GetCursorPos(out var cursor) || (cursor.X, cursor.Y) == _lastCursor) return;
        _lastCursor = (cursor.X, cursor.Y);
        _moved = true;
        DragTo(cursor.X, cursor.Y);
    }

    private void OnGripReleased(object sender, PointerRoutedEventArgs e)
    {
        _grip.ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragTimer.Stop();
        FollowCursor();
        _dragging = false;
        if (_moved) PlacementChanged?.Invoke(_dock, _centerX, _centerY);
    }

    private void DragTo(int x, int y)
    {
        var scale = Scale;
        var next = NearestEdge(x, y, scale);
        var turned = (next is ToolbarDock.Left or ToolbarDock.Right) != IsVertical;
        _dock = next;
        if (turned)
        {
            ApplyOrientation();
            // The grip is in the bar's corner in both orientations: keep it under the pointer.
            _grab = ((int)(ThicknessDip / 2 * scale), (int)(ThicknessDip / 2 * scale));
        }

        var (width, height) = PixelSize(scale);
        var middleX = x - _grab.X + width / 2;
        var middleY = y - _grab.Y + height / 2;
        _centerX = (middleX - _monitor.X) / (double)_monitor.Width;
        _centerY = (middleY - _monitor.Y) / (double)_monitor.Height;

        var snap = CenterSnapDip * scale;
        if (_dock is ToolbarDock.Top or ToolbarDock.Bottom && Math.Abs(middleX - (_monitor.X + _monitor.Width / 2.0)) <= snap) _centerX = 0.5;
        if (_dock is ToolbarDock.Left or ToolbarDock.Right && Math.Abs(middleY - (_monitor.Y + _monitor.Height / 2.0)) <= snap) _centerY = 0.5;

        Place();
        if (turned) WatchFirstLayout();
    }

    private ToolbarDock NearestEdge(int x, int y, double scale)
    {
        var best = ToolbarDock.Floating;
        var bestDistance = double.MaxValue;
        void Consider(ToolbarDock edge, int distance)
        {
            var limit = (edge == _dock ? UndockDistanceDip : DockDistanceDip) * scale;
            if (distance <= limit && distance < bestDistance) { best = edge; bestDistance = distance; }
        }

        Consider(ToolbarDock.Top, y - _monitor.Y);
        Consider(ToolbarDock.Bottom, _monitor.Y + _monitor.Height - 1 - y);
        Consider(ToolbarDock.Left, x - _monitor.X);
        Consider(ToolbarDock.Right, _monitor.X + _monitor.Width - 1 - x);
        return best;
    }

    private double Scale
    {
        get
        {
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            return scale > 0 ? scale : 1;
        }
    }

    private void ApplyOrientation()
    {
        var vertical = IsVertical;
        _root.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        // Centred in the window's real client area, which the border makes smaller than the size asked for,
        // so the space around the row is equal on both sides and above and below.
        _root.HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        _root.VerticalAlignment = vertical ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        _grip.Height = vertical ? 36 : double.NaN;
        _gripIcon.Glyph = vertical ? "" : "";
        _gripContent.Padding = vertical ? new Thickness(0) : new Thickness(12, 0, 0, 0);
        _name.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        _bar.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        _column.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Real sizes are only known after an orientation's first layout pass, so place it again once they are.</summary>
    private void WatchFirstLayout()
    {
        if ((IsVertical ? _columnSize : _rowSize).Width > 0) return;
        _root.LayoutUpdated -= OnFirstLayout;
        _root.LayoutUpdated += OnFirstLayout;
    }

    private void OnFirstLayout(object? sender, object e)
    {
        if (_root.ActualWidth <= 0 || _root.ActualHeight <= 0) return;
        _root.LayoutUpdated -= OnFirstLayout;
        _root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (IsVertical) _columnSize = _root.DesiredSize;
        else _rowSize = _root.DesiredSize;
        Place();
    }

    private (int Width, int Height) PixelSize(double scale)
    {
        var widthDip = IsVertical ? ThicknessDip : _rowSize.Width > 0 ? _rowSize.Width + 8 : 900;
        var heightDip = !IsVertical ? ThicknessDip : _columnSize.Height > 0 ? _columnSize.Height : 420;
        return (Math.Max((int)(widthDip * scale), _minimumWidth), (int)(heightDip * scale));
    }

    private void Place()
    {
        var scale = Scale;
        var (w, h) = PixelSize(scale);
        var margin = (int)(EdgeMarginDip * scale);

        var x = (int)(_monitor.X + _centerX * _monitor.Width - w / 2.0);
        var y = (int)(_monitor.Y + _centerY * _monitor.Height - h / 2.0);
        switch (_dock)
        {
            case ToolbarDock.Top: y = _monitor.Y + margin; break;
            case ToolbarDock.Bottom: y = _monitor.Y + _monitor.Height - h - margin; break;
            case ToolbarDock.Left: x = _monitor.X + margin; break;
            case ToolbarDock.Right: x = _monitor.X + _monitor.Width - w - margin; break;
        }

        x = Math.Clamp(x, _monitor.X + margin, Math.Max(_monitor.X + margin, _monitor.X + _monitor.Width - w - margin));
        y = Math.Clamp(y, _monitor.Y + margin, Math.Max(_monitor.Y + margin, _monitor.Y + _monitor.Height - h - margin));
        var current = AppWindow.Size;
        if (current.Width == w && current.Height == h)
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
            return;
        }
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));

        // Asked for less than Windows allows: learn the real width, and place again so the edge margin is right.
        var actual = AppWindow.Size.Width;
        if (actual > w)
        {
            _minimumWidth = actual;
            Place();
        }
    }

    /// <summary>The drag handle. A separate class because the pointer shape can only be set from inside a control.</summary>
    private sealed class Grip : Grid
    {
        public Grip()
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // transparent, not null: stays clickable
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        }
    }

    private const uint WM_GETMINMAXINFO = 0x24;
    private const int MinTrackSizeOffset = 24; // MINMAXINFO.ptMinTrackSize

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
