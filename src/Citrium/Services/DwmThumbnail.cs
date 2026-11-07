using System.Runtime.InteropServices;

namespace Citrium.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

/// <summary>
/// A live picture of another window, composed by DWM. It scales the picture and leaves the source alone, so Citrix
/// never sees a resize. DWM draws it above the destination window's content, so nothing in XAML can sit on top of
/// it, and it takes no input: clicks land on whatever XAML is underneath.
/// </summary>
internal sealed class DwmThumbnail : IDisposable
{
    private IntPtr _handle;

    private DwmThumbnail(IntPtr handle) => _handle = handle;

    public static DwmThumbnail? TryCreate(IntPtr destination, IntPtr source)
    {
        var hr = DwmRegisterThumbnail(destination, source, out var handle);
        if (hr == 0) return new DwmThumbnail(handle);
        Log.Info($"Couldn't start a live view (0x{hr:X8})");
        return null;
    }

    /// <summary>The largest rectangle with the source's shape that fits inside <paramref name="box"/>, centered.</summary>
    public static NativeRect Letterbox(NativeRect source, NativeRect box)
    {
        var fit = Math.Min(box.Width / (double)source.Width, box.Height / (double)source.Height);
        int width = (int)(source.Width * fit), height = (int)(source.Height * fit);
        int left = box.Left + (box.Width - width) / 2, top = box.Top + (box.Height - height) / 2;
        return new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    /// <summary>Shows <paramref name="source"/> in <paramref name="destination"/>, cut off at <paramref name="clip"/> if it's given.</summary>
    public void Show(NativeRect destination, NativeRect source, NativeRect? clip = null)
    {
        if (_handle == IntPtr.Zero || destination.Width <= 0 || destination.Height <= 0 || source.Width <= 0 || source.Height <= 0) return;

        if (clip is { } c)
        {
            var visible = new NativeRect
            {
                Left = Math.Max(destination.Left, c.Left), Top = Math.Max(destination.Top, c.Top),
                Right = Math.Min(destination.Right, c.Right), Bottom = Math.Min(destination.Bottom, c.Bottom),
            };
            if (visible.Width <= 0 || visible.Height <= 0) { Hide(); return; }

            // DWM only scales, so the source is cut by the same share as the destination.
            double scaleX = source.Width / (double)destination.Width, scaleY = source.Height / (double)destination.Height;
            source = new NativeRect
            {
                Left = source.Left + (int)((visible.Left - destination.Left) * scaleX),
                Top = source.Top + (int)((visible.Top - destination.Top) * scaleY),
                Right = source.Left + (int)((visible.Right - destination.Left) * scaleX),
                Bottom = source.Top + (int)((visible.Bottom - destination.Top) * scaleY),
            };
            destination = visible;
        }

        var properties = new Properties
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = destination,
            rcSource = source,
            fVisible = 1,
            fSourceClientAreaOnly = 1,
        };
        var hr = DwmUpdateThumbnailProperties(_handle, ref properties);
        if (hr != 0) Log.Info($"Couldn't update a live view (0x{hr:X8})");
    }

    public void Hide()
    {
        if (_handle == IntPtr.Zero) return;
        var properties = new Properties { dwFlags = DWM_TNP_VISIBLE, fVisible = 0 };
        DwmUpdateThumbnailProperties(_handle, ref properties);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>Where <paramref name="child"/> sits in <paramref name="window"/>'s client area. Works across processes.</summary>
    public static NativeRect ChildRect(IntPtr window, IntPtr child)
    {
        GetWindowRect(child, out var area);
        var origin = new Point();
        ClientToScreen(window, ref origin);
        return new NativeRect
        {
            Left = area.Left - origin.X, Top = area.Top - origin.Y,
            Right = area.Right - origin.X, Bottom = area.Bottom - origin.Y,
        };
    }

    private const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Properties
    {
        public uint dwFlags;
        public NativeRect rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref Properties properties);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
}
