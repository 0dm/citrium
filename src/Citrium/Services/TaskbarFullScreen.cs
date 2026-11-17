using System.Runtime.InteropServices;

namespace Citrium.Services;

/// <summary>
/// Tells the taskbar that a window is full screen, so the taskbar stays out of the way whenever that window is active.
/// Windows otherwise guesses from the window covering its monitor, and it misses a window that comes back from being
/// cloaked (picture in picture): the window is full screen again, but the taskbar stays on top of it.
/// Called through the vtable as <see cref="JumpList"/> is, for the same reason.
/// </summary>
internal static unsafe class TaskbarFullScreen
{
    private static readonly Guid TaskbarListClass = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private static readonly Guid ITaskbarList2 = new("602D4995-B13A-429B-A66E-1935E44F4317");
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const int SlotHrInit = 3, SlotMarkFullscreenWindow = 8;

    private static IntPtr _taskbar;

    public static void Mark(IntPtr hwnd, bool fullScreen)
    {
        try
        {
            if (_taskbar == IntPtr.Zero)
            {
                IntPtr instance = IntPtr.Zero;
                var classId = TaskbarListClass;
                var interfaceId = ITaskbarList2;
                var hr = CoCreateInstance(&classId, IntPtr.Zero, CLSCTX_INPROC_SERVER, &interfaceId, &instance);
                if (hr < 0) throw new InvalidOperationException($"CoCreateInstance failed: 0x{hr:X8}");
                hr = ((delegate* unmanaged<IntPtr, int>)Slot(instance, SlotHrInit))(instance);
                if (hr < 0) throw new InvalidOperationException($"HrInit failed: 0x{hr:X8}");
                _taskbar = instance;
            }
            var result = ((delegate* unmanaged<IntPtr, IntPtr, int, int>)Slot(_taskbar, SlotMarkFullscreenWindow))(_taskbar, hwnd, fullScreen ? 1 : 0);
            if (result < 0) Log.Info($"MarkFullscreenWindow failed: 0x{result:X8}");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't tell the taskbar about full screen", ex);
        }
    }

    private static IntPtr Slot(IntPtr comObject, int slot) => (*(IntPtr**)comObject)[slot];

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* instance);
}
