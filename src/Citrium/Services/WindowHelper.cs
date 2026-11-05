using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Citrium.Services;

internal static class WindowHelper
{
    private static readonly List<(Window Window, FrameworkElement Root)> Windows = [];

    public static void ResizeCentered(Window window, int width, int height)
    {
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetScale(window);
        var w = (int)(width * scale);
        var h = (int)(height * scale);
        w = Math.Min(w, area.Width);
        h = Math.Min(h, area.Height);
        var x = area.X + (area.Width - w) / 2;
        var y = area.Y + (area.Height - h) / 2;
        // MoveAndResize stores the rect full screen restores, but the first show does not move the window.
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static double GetScale(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void ApplyTheme(Window window, FrameworkElement root)
    {
        if (!Windows.Any(w => w.Window == window))
        {
            Windows.Add((window, root));
            window.Closed += (_, _) => Windows.RemoveAll(w => w.Window == window);
            root.ActualThemeChanged += (_, _) => UpdateCaptionButtons(window, root);
        }

        root.RequestedTheme = AppState.Current.Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        window.SystemBackdrop = AppState.Current.Settings.Backdrop switch
        {
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "MicaAlt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            _ => new MicaBackdrop { Kind = MicaKind.Base },
        };
        UpdateCaptionButtons(window, root);
    }

    public static void ApplyThemeToAll()
    {
        foreach (var (w, r) in Windows.ToList()) ApplyTheme(w, r);
    }

    private static void UpdateCaptionButtons(Window window, FrameworkElement root)
    {
        var tb = window.AppWindow.TitleBar;
        var dark = root.ActualTheme == ElementTheme.Dark;
        var fg = dark ? Colors.White : Colors.Black;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0);
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x08, 0, 0, 0);
        tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0, 0, 0);
    }
}
