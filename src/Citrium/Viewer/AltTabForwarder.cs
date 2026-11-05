using System.Runtime.InteropServices;
using Citrium.Services;

namespace Citrium.Viewer;

/// <summary>
/// Keeps Alt+Tab inside the remote desktop. Citrix only captures it when Desktop Viewer itself is maximized,
/// and Citrium keeps that window hidden, so Windows would switch away from the session. This low-level keyboard
/// hook (in Citrium's own process) swallows the physical Tab while <paramref name="shouldForward"/> says the
/// session owns the keyboard, and asks the viewer to send Citrix's Alt+Tab hotkey to the session instead.
/// </summary>
internal sealed class AltTabForwarder : IDisposable
{
    private readonly Func<bool> _shouldForward;
    private readonly Action<bool> _forward;
    private readonly HookProc _proc; // kept in a field: the native hook holds no reference to the delegate
    private IntPtr _hook;
    private bool _swallowedDown;

    public AltTabForwarder(Func<bool> shouldForward, Action<bool> forward)
    {
        _shouldForward = shouldForward;
        _forward = forward;
        _proc = OnKey;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) Log.Error($"Couldn't install the Alt+Tab hook (error {Marshal.GetLastWin32Error()})");
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(data);
            var msg = (int)message;
            if (key.vkCode == VK_TAB && (key.flags & LLKHF_INJECTED) == 0)
            {
                if (msg is WM_KEYUP or WM_SYSKEYUP)
                {
                    // The matching release of a Tab we took, so Windows never sees half a keypress.
                    if (_swallowedDown) { _swallowedDown = false; return 1; }
                }
                else if ((key.flags & LLKHF_ALTDOWN) != 0 && _shouldForward())
                {
                    _swallowedDown = true;
                    _forward((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0);
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private const int WH_KEYBOARD_LL = 13, WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105, VK_TAB = 0x09, VK_SHIFT = 0x10;
    private const uint LLKHF_INJECTED = 0x10, LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public int vkCode; public int scanCode; public uint flags; public uint time; public IntPtr extra; }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
