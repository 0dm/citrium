using System.Runtime.InteropServices;

namespace Citrium.Services;

/// <summary>Tells Citrium when Windows locks and unlocks, through the main window's messages.</summary>
internal sealed class LockWatcher : IDisposable
{
    private const uint WM_WTSSESSION_CHANGE = 0x2B1;
    private const int WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;
    private const nuint SubclassId = 0x6C6B;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc; // a field: Windows holds the only other reference, and the GC can't see that
    private bool _disposed;

    public event Action? Locked;
    public event Action? Unlocked;

    public LockWatcher(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = OnMessage;
        SetWindowSubclass(_hwnd, _proc, SubclassId, 0);
        if (!WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION)) Log.Info("Couldn't watch for Windows locking");
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WM_WTSSESSION_CHANGE)
        {
            if (wParam.ToInt64() == WTS_SESSION_LOCK) Locked?.Invoke();
            else if (wParam.ToInt64() == WTS_SESSION_UNLOCK) Unlocked?.Invoke();
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WTSUnRegisterSessionNotification(_hwnd);
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
