using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Citrium.Services;

/// <summary>A row in the tray menu: a command, a greyed-out heading if it has no action, or a submenu.</summary>
internal sealed record TrayMenuItem(string Text, Action? Invoke = null, IReadOnlyList<TrayMenuItem>? Children = null);

/// <summary>
/// Notification-area (system tray) icon for the main window, using Shell_NotifyIcon directly.
/// Left-click opens Citrium; right-click shows what's running, your favorites and Quit. Survives Explorer
/// restarts (TaskbarCreated).
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 0x42; // WM_APP + n
    private const uint IconId = 1;
    private const int MenuOpen = 1, MenuQuit = 2, MenuFirstDynamic = 100;

    private static TrayIcon? _instance; // the subclass proc is static; one tray icon per process
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly IntPtr _notificationIcon;
    private bool _added;
    private string _tip = "Citrium";
    private Action? _notificationClicked;

    /// <summary>The rows above Open and Quit, built fresh each time the menu opens.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuItems { get; set; }

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    public TrayIcon(IntPtr hwnd, string iconPath)
    {
        _hwnd = hwnd;
        _icon = LoadImageW(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */,
            GetSystemMetrics(49 /* SM_CXSMICON */), GetSystemMetrics(50 /* SM_CYSMICON */), 0x10 /* LR_LOADFROMFILE */);
        // Its own size: asked for a large icon and handed a small one, Windows refuses the whole notification.
        _notificationIcon = LoadImageW(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */,
            GetSystemMetrics(11 /* SM_CXICON */), GetSystemMetrics(12 /* SM_CYICON */), 0x10 /* LR_LOADFROMFILE */);
        _instance = this;
        SetWindowSubclass(_hwnd, &SubclassProc, 0x7472 /* id */, IntPtr.Zero);
    }

    public bool IsVisible => _added;

    public void Show()
    {
        if (_added) return;
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_added) { Log.Error("Shell_NotifyIcon(NIM_ADD) failed"); return; }
        data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4: NIN_SELECT / WM_CONTEXTMENU with coordinates
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    public void Hide()
    {
        if (!_added) return;
        var data = NewData(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <param name="clicked">Runs if the user clicks the notification. Otherwise a click just opens Citrium.</param>
    public void Notify(string title, string message, Action? clicked = null)
    {
        if (!_added) return;
        _notificationClicked = clicked;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = 0x4 /* NIIF_USER */ | 0x20 /* NIIF_LARGE_ICON */;
        data.hBalloonIcon = _notificationIcon;
        if (!Shell_NotifyIconW(NIM_MODIFY, ref data)) Log.Error($"Windows refused the notification \"{title}\"");
    }

    private NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, IntPtr refData)
    {
        var self = _instance;
        if (self is not null)
        {
            try
            {
                if (msg == CallbackMessage)
                {
                    // Version 4: LOWORD(lParam) is the notification, wParam holds the anchor point.
                    switch ((uint)(lParam.ToInt64() & 0xFFFF))
                    {
                        case 0x0400: // NIN_SELECT (left click / Enter)
                        case 0x0401: // NIN_KEYSELECT
                        case 0x0203: // WM_LBUTTONDBLCLK
                            self.OpenRequested?.Invoke();
                            break;
                        case 0x0405: // NIN_BALLOONUSERCLICK
                            var clicked = self._notificationClicked ?? self.OpenRequested;
                            self._notificationClicked = null;
                            clicked?.Invoke();
                            break;
                        case 0x007B: // WM_CONTEXTMENU
                            self.ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                            break;
                    }
                    return IntPtr.Zero;
                }
                if (msg == TaskbarCreated && self._added)
                {
                    // Explorer restarted: the icon is gone, add it back.
                    self._added = false;
                    self.Show();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Tray message handling failed", ex);
            }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        var commands = new List<Action>();
        try
        {
            if (MenuItems?.Invoke() is { Count: > 0 } items)
            {
                Append(menu, items, commands);
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
            }
            AppendMenuW(menu, 0, MenuOpen, "Open Citrium");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, 0, MenuQuit, "Quit");
            SetMenuDefaultItem(menu, MenuOpen, 0);
            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0002 /* TPM_RIGHTBUTTON */ | 0x0008 /* TPM_BOTTOMALIGN */, x, y, _hwnd, IntPtr.Zero);
            if (cmd == MenuOpen) OpenRequested?.Invoke();
            else if (cmd == MenuQuit) QuitRequested?.Invoke();
            else if (cmd >= MenuFirstDynamic && cmd - MenuFirstDynamic < commands.Count) commands[cmd - MenuFirstDynamic]();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void Append(IntPtr menu, IReadOnlyList<TrayMenuItem> items, List<Action> commands)
    {
        foreach (var item in items)
        {
            var text = item.Text.Replace("&", "&&"); // a lone & would underline the next letter
            if (item.Children is { Count: > 0 } children)
            {
                // Windows destroys a submenu along with the menu that holds it.
                var submenu = CreatePopupMenu();
                Append(submenu, children, commands);
                AppendMenuW(menu, MF_POPUP, (nuint)submenu.ToInt64(), text);
            }
            else if (item.Invoke is { } invoke)
            {
                commands.Add(invoke);
                AppendMenuW(menu, 0, (nuint)(MenuFirstDynamic + commands.Count - 1), text);
            }
            else
            {
                AppendMenuW(menu, MF_GRAYED, 0, text);
            }
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        RemoveWindowSubclass(_hwnd, &SubclassProc, 0x7472);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        if (_notificationIcon != IntPtr.Zero) DestroyIcon(_notificationIcon);
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private const uint MF_GRAYED = 0x1, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, nuint, IntPtr, IntPtr> proc, nuint id, IntPtr refData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, nuint, IntPtr, IntPtr> proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? item);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPos);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
