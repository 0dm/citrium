using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Citrium.Services;

namespace Citrium.Viewer;

/// <summary>
/// Talks to a running session through Citrix's own session API (CCM), the route Citrix's toolbar uses for its
/// Ctrl+Alt+Del button. Keys typed or injected through Windows can't do this: Windows keeps a real Ctrl+Alt+Del
/// for itself. Posted through the API they go straight to the engine, which forwards them to the desktop.
/// </summary>
/// <remarks>
/// The COM interfaces are called through their vtables: Citrium's trimmed, CsWinRT-hosted runtime has no classic
/// COM interop for them. They derive directly from IUnknown, so a method's slot is 3 plus its position in the interface.
/// </remarks>
internal static unsafe partial class CitrixSessionApi
{
    private static readonly Guid CcmCtrlClass = new("A87FE8ED-0B20-4D00-B497-D1B54F176231");
    private static readonly Guid CcmCtrl2 = new("9C25DF55-32BE-46E6-A0BE-844F4C937651");
    private static readonly Guid PrivateSessionControl = new("DAE32A9F-DC07-4EE1-8EB1-944E060694F4");

    private const int MaxConnections = 4;
    private const int ConnectionRetryDelayMs = 150;
    private const int SlotRelease = 2;
    private const int SlotCreateConnection = 12;
    private const int SlotRemoveConnection = 13;
    private const int SlotPostMessage = 42; // on IConnectionCtrl_PrivateICO

    private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
    private const uint VK_CONTROL = 0x11, VK_MENU = 0x12, VK_DELETE = 0x2E;
    private const uint KeyDownFlags = 1, KeyUpFlags = 0xC0000001;

    /// <summary>
    /// The CCM id of the session shown in Desktop Viewer window <paramref name="frame"/>. Citrix starts one toolbar
    /// process per session and passes it both: <c>-session:&lt;id&gt; -parent:&lt;frame in hex&gt;</c>.
    /// </summary>
    public static int? FindSessionId(IntPtr frame)
    {
        var toolbars = new List<string>();
        foreach (var p in Process.GetProcessesByName("Citrix.EngineUI.App"))
        {
            using (p)
            {
                if (ReadCommandLine(p.Id) is { } line) toolbars.Add(line);
            }
        }

        foreach (var line in toolbars)
        {
            var parent = ParentPattern().Match(line);
            if (parent.Success && long.TryParse(parent.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var hwnd)
                && hwnd == frame.ToInt64())
            {
                return ParseSession(line);
            }
        }
        return toolbars.Count == 1 ? ParseSession(toolbars[0]) : null;
    }

    private static int? ParseSession(string commandLine)
    {
        // Ids are unsigned and often exceed int.MaxValue; the API takes the same 32 bits as a signed value.
        var match = SessionPattern().Match(commandLine);
        return match.Success && uint.TryParse(match.Groups[1].Value, out var id) ? unchecked((int)id) : null;
    }

    /// <summary>Sends Ctrl+Alt+Del to the session the way Citrix's toolbar does. False if the API refused it.</summary>
    public static bool SendCtrlAltDel(int sessionId) => WithSessionControl(control =>
    {
        // Same order and flags as Citrix's: presses go to the engine window, releases to its child.
        if (!PostKey(control, sessionId, VK_CONTROL, up: false)) return false; // nothing pressed yet, safe to retry
        var sent = PostKey(control, sessionId, VK_MENU, up: false) && PostKey(control, sessionId, VK_DELETE, up: false);

        // Always released, whatever happened above: a stuck Ctrl would corrupt everything typed afterwards.
        PostKey(control, sessionId, VK_DELETE, up: true);
        PostKey(control, sessionId, VK_MENU, up: true);
        PostKey(control, sessionId, VK_CONTROL, up: true);
        return sent;
    });

    private static int _lastResult;

    private static bool PostKey(IntPtr control, int sessionId, uint vk, bool up)
    {
        var scan = (MapVirtualKeyW(vk, 0) & 0xFF) << 16;
        var post = (delegate* unmanaged<IntPtr, int, uint, uint, uint, sbyte, int>)Slot(control, SlotPostMessage);
        _lastResult = post(control, sessionId, up ? WM_KEYUP : WM_KEYDOWN, vk, scan | (up ? KeyUpFlags : KeyDownFlags), up ? (sbyte)1 : (sbyte)0);
        return _lastResult >= 0;
    }

    private static readonly object ConnectionGate = new();
    private static IntPtr _ctrl, _connection, _control, _mtaCookie;

    /// <summary>
    /// Runs <paramref name="action"/> on the process's API connection, opening it if needed. It stays open between
    /// commands, as Citrix's toolbar keeps its own. The engine turns down the first connection a process opens
    /// (0x8004EFFF, however long it's given) and accepts one made a moment later, and a connection goes stale if the
    /// session reconnects, so a refusal replaces it. It's never closed: it lasts as long as this viewer's session.
    /// Callers must be on a background thread: the connection lives in the multithreaded apartment.
    /// </summary>
    private static bool WithSessionControl(Func<IntPtr, bool> action)
    {
        lock (ConnectionGate)
        {
            for (var attempt = 1; attempt <= MaxConnections; attempt++)
            {
                try
                {
                    if (EnsureConnection() && action(_control)) return true;
                }
                catch (Exception ex)
                {
                    Log.Error("Citrix session API failed", ex);
                }
                CloseConnection();
                if (attempt < MaxConnections) Thread.Sleep(ConnectionRetryDelayMs);
            }
            Log.Info($"Citrix refused the session command: 0x{_lastResult:X8}");
            return false;
        }
    }

    private static bool EnsureConnection()
    {
        if (_control != IntPtr.Zero) return true;

        // Keeps the apartment alive between commands, which run on whichever pool thread is free.
        if (_mtaCookie == IntPtr.Zero)
        {
            var mta = CoIncrementMTAUsage(out _mtaCookie);
            if (mta < 0) { Log.Info($"Citrix session API has no apartment: 0x{mta:X8}"); return false; }
        }

        var name = Marshal.StringToCoTaskMemAnsi("Citrium");
        try
        {
            IntPtr ctrl = IntPtr.Zero, connection = IntPtr.Zero;
            var clsid = CcmCtrlClass;
            var iid = CcmCtrl2;
            var hr = CoCreateInstance(&clsid, IntPtr.Zero, 0x17 /* CLSCTX_ALL */, &iid, &ctrl);
            if (hr < 0) { Log.Info($"Citrix session API unavailable: 0x{hr:X8}"); return false; }
            _ctrl = ctrl;

            var create = (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(_ctrl, SlotCreateConnection);
            hr = create(_ctrl, name, &connection);
            if (hr < 0 || connection == IntPtr.Zero) { Log.Info($"Citrix session API refused a connection: 0x{hr:X8}"); return false; }
            _connection = connection;

            var controlIid = PrivateSessionControl;
            hr = Marshal.QueryInterface(_connection, in controlIid, out _control);
            if (hr < 0) { Log.Info($"Citrix session control unavailable: 0x{hr:X8}"); _control = IntPtr.Zero; return false; }

            Log.Info("Opened the Citrix session connection");
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    private static void CloseConnection()
    {
        if (_control != IntPtr.Zero) Marshal.Release(_control);
        if (_connection != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(_ctrl, SlotRemoveConnection))(_ctrl, _connection);
            Marshal.Release(_connection);
        }
        if (_ctrl != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Slot(_ctrl, SlotRelease))(_ctrl);
        _control = _connection = _ctrl = IntPtr.Zero;
    }

    private static IntPtr Slot(IntPtr comObject, int slot) => (*(IntPtr**)comObject)[slot];

    private static string? ReadCommandLine(int pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new byte[8192];
            fixed (byte* data = buffer)
            {
                if (NtQueryInformationProcess(process, 60 /* ProcessCommandLineInformation */, data, buffer.Length, out _) != 0) return null;
                var text = (UnicodeString*)data;
                return new string(text->Buffer, 0, text->Length / 2);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [GeneratedRegex(@"-parent:([0-9A-Fa-f]+)")] private static partial Regex ParentPattern();
    [GeneratedRegex(@"-session:(\d+)")] private static partial Regex SessionPattern();

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public char* Buffer; }

    [DllImport("ole32.dll")] private static extern int CoIncrementMTAUsage(out IntPtr cookie);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* instance);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, void* info, int length, out int returned);
}
