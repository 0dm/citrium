using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Citrium.Services;

namespace Citrium.Viewer;

/// <summary>
/// Shows a Citrix desktop session inside a Citrium window.
///
/// The session is launched the normal way (wfica32.exe + ICA file), so everything Citrix supports keeps
/// working, including single sign-on. Citrix shows sessions in its Desktop Viewer: a frame window (in
/// Citrix.DesktopViewer.App / CDViewer) that hosts wfica32's session surface as a cross-process child. Citrium
/// takes the frame's place: it re-parents that surface into its own window and hides Desktop Viewer's frame
/// and toolbar. Only standard window-management calls are used; nothing is injected into Citrix processes.
/// </summary>
internal sealed class SessionSurfaceHost : IDisposable
{
    private static readonly string[] FrameProcesses = ["Citrix.DesktopViewer.App", "CDViewer", HdxEngine.ProcessName];

    private readonly string _title;
    private IntPtr _frame;
    private IntPtr _surface;
    private int _surfacePid;
    private bool _disposed;

    public IntPtr Container { get; }

    public int SurfaceProcessId => _surfacePid;

    public Process? Engine { get; private set; }

    public bool IsAdopted => _surface != IntPtr.Zero && IsWindow(_surface) && GetParent(_surface) == Container;

    public SessionSurfaceHost(IntPtr parentWindow, string title)
    {
        _title = title;
        // SS_BLACKRECT: the container itself paints black behind the session surface (letterboxing).
        Container = CreateWindowExW(0, "Static", "", WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | SS_BLACKRECT,
            0, 0, 16, 16, parentWindow, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Container == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the session container");
    }

    public void Launch(string icaFile, string? enginePath)
    {
        Engine = HdxEngine.LaunchFile(icaFile, HdxEngine.Locate(enginePath));
    }

    public bool IsSessionAlive
    {
        get
        {
            if (_surface != IntPtr.Zero && IsWindow(_surface)) return true;
            if (_surfacePid != 0 && ProcessAlive(_surfacePid)) return true;
            return Engine is { } p && !HasExited(p);
        }
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        SetWindowPos(Container, HWND_TOP, x, y, Math.Max(1, width), Math.Max(1, height), SWP_NOACTIVATE);
        FitSurface();
    }

    public void Show(bool visible) => ShowWindow(Container, visible ? SW_SHOWNA : SW_HIDE);

    /// <summary>
    /// Watchdog step: finds Desktop Viewer's frame for this session, adopts its session surface, keeps it sized,
    /// and keeps Citrix's frame/toolbar hidden. Returns true while the surface is ours.
    /// </summary>
    public bool EnsureAdopted()
    {
        if (IsAdopted)
        {
            FitSurface();
            HideCitrixChrome();
            return true;
        }

        FindFrame();
        var surface = FindSurfaceInFrame();
        if (surface == IntPtr.Zero)
        {
            HideCitrixChrome();
            return false;
        }

        var style = GetWindowLongPtrW(surface, GWL_STYLE).ToInt64();
        style = (style | WS_CHILD | WS_VISIBLE) & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME);
        SetWindowLongPtrW(surface, GWL_STYLE, new IntPtr(style));
        SetParent(surface, Container);
        GetWindowThreadProcessId(surface, out var pid);
        _surface = surface;
        _surfacePid = (int)pid;
        FitSurface(force: true);
        HideCitrixChrome();
        Log.Info($"Adopted session surface 0x{surface.ToInt64():X} (pid {pid}) from \"{TextOf(_frame)}\" [{ClassOf(_frame)}]");
        return true;
    }

    public void Focus()
    {
        if (!IsAdopted) return;
        // The surface's thread input is attached to ours via the parent/child relationship.
        SetFocus(_surface);
    }

    /// <summary>
    /// Sends Ctrl+Alt+Del to the remote desktop through Citrix's session API, as Citrix's own toolbar button does.
    /// A real Ctrl+Alt+Del never leaves Windows. Citrix's Ctrl+F1 hotkey stays as the fallback if the API isn't
    /// available; the engine only sees that as normal keyboard input, so the session has to hold the keyboard.
    /// </summary>
    public async Task SendCtrlAltDelAsync()
    {
        await TakeKeyboardAsync();
        var frame = _frame;
        var sent = await Task.Run(() =>
        {
            if (CitrixSessionApi.FindSessionId(frame) is { } session) return CitrixSessionApi.SendCtrlAltDel(session);
            Log.Info("Couldn't find this session's Citrix id");
            return false;
        });
        if (sent)
        {
            Log.Info("Sent Ctrl+Alt+Del through the Citrix session API");
            return;
        }

        var hotkey = CitrixHotkeys.CtrlAltDel;
        SendKeys((hotkey.ModifierVk, false), (hotkey.KeyVk, false), (hotkey.KeyVk, true), (hotkey.ModifierVk, true));
        Log.Info("Sent Citrix's Ctrl+Alt+Del hotkey");
    }

    /// <summary>
    /// Sends Citrix's Alt+Tab hotkey. Called while the user is physically holding Alt, so only the key
    /// itself is sent: pressing and releasing a modifier here would drop out of the task switcher.
    /// </summary>
    public async Task SendAltTabAsync(bool reverse)
    {
        var hotkey = reverse ? CitrixHotkeys.AltShiftTab : CitrixHotkeys.AltTab;
        await TakeKeyboardAsync();
        SendKeys((hotkey.KeyVk, false), (hotkey.KeyVk, true));
        Log.Info(reverse ? "Forwarded Shift+Alt+Tab to the session" : "Forwarded Alt+Tab to the session");
    }

    private async Task TakeKeyboardAsync()
    {
        var top = GetAncestor(Container, GA_ROOT);
        if (GetForegroundWindow() != top) ForceForeground(top);
        Focus();

        // Keys sent before focus has really arrived go nowhere (a title bar button leaves it in WinUI),
        // and the engine needs a moment to start reading the keyboard once it has it.
        var deadline = DateTime.UtcNow.AddMilliseconds(300);
        while (!SessionHasKeyboard(top) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
            Focus();
        }
        await Task.Delay(80);
        Log.Info($"Keyboard for session: foreground={(GetForegroundWindow() == top ? "viewer" : ClassOf(GetForegroundWindow()))} focus={(SessionHasKeyboard(top) ? "session" : "NOT session")}");
    }

    private bool SessionHasKeyboard(IntPtr top)
    {
        if (GetForegroundWindow() != top) return false;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(top, out _), ref info)) return false;
        return info.hwndFocus == _surface || IsChild(_surface, info.hwndFocus);
    }

    /// <summary>
    /// Desktop Viewer activates its frame when a session connects. That frame is hidden here, and it keeps the
    /// foreground, so everything typed goes to an invisible window. Once Citrium's window is foreground, focus
    /// lands on the session by itself because the two windows share input.
    /// </summary>
    public void ReclaimForegroundFromCitrix()
    {
        if (_frame == IntPtr.Zero || _gaveUp) return;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        if (GetAncestor(fg, GA_ROOT) != _frame && GetWindow(fg, GW_OWNER) != _frame) return;
        ForceForeground(GetAncestor(Container, GA_ROOT));
    }

    /// <summary>
    /// Clicking the session activates Citrium's window, and WinUI then gives keyboard focus to its own input
    /// window instead of the session, so typing stops until something moves it back. While Citrium is
    /// foreground the keyboard belongs to the session: its title bar buttons are all mouse-driven.
    /// </summary>
    public void KeepKeyboardOnSession()
    {
        if (!IsAdopted) return;
        var top = GetAncestor(Container, GA_ROOT);
        if (GetForegroundWindow() == top && !SessionHasKeyboard(top)) SetFocus(_surface);
    }

    /// <summary>Moves the foreground to <paramref name="window"/>; Windows only allows that from the current foreground thread's input queue.</summary>
    private static void ForceForeground(IntPtr window)
    {
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(window);
        SetForegroundWindow(window);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    /// <summary>
    /// Disconnects: ends the local engine. The desktop keeps running on the server (per the site's policy)
    /// and reconnects next time it's opened, exactly like closing Citrix Desktop Viewer.
    /// </summary>
    public async Task DisconnectAsync()
    {
        Log.Info($"Disconnecting {_title}");
        if (_frame != IntPtr.Zero && IsWindow(_frame))
        {
            PostMessageW(_frame, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            for (var i = 0; i < 40 && IsSessionAlive; i++) await Task.Delay(100);
            if (!IsSessionAlive) { Log.Info("Disconnected via Desktop Viewer"); return; }
        }

        foreach (var pid in new[] { _surfacePid, Engine?.Id ?? 0 }.Where(p => p != 0).Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.ProcessName.Equals(HdxEngine.ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                Log.Info($"Disconnected by ending wfica32 (pid {pid})");
            }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex) { Log.Error($"Couldn't end wfica32 (pid {pid})", ex); }
        }
    }

    /// <summary>Desktop Viewer titles its frame "&lt;resource name&gt; - Desktop Viewer".</summary>
    private void FindFrame()
    {
        if (_frame != IntPtr.Zero && IsWindow(_frame)) return;
        var pids = new HashSet<uint>();
        foreach (var name in FrameProcesses)
            foreach (var p in Process.GetProcessesByName(name)) using (p) pids.Add((uint)p.Id);
        if (pids.Count == 0) return;

        var prefix = _title + " - ";
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var t = TextOf(h);
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || t.Equals(_title, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            _frame = found;
            Log.Info($"Found Citrix session frame 0x{found.ToInt64():X} \"{TextOf(found)}\" [{ClassOf(found)}]");
        }
    }

    /// <summary>The largest window inside the frame that belongs to a wfica32 process: the session surface.</summary>
    private IntPtr FindSurfaceInFrame()
    {
        if (_frame == IntPtr.Zero || !IsWindow(_frame)) return IntPtr.Zero;
        var engines = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(HdxEngine.ProcessName)) using (p) engines.Add((uint)p.Id);

        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        void Walk(IntPtr parent)
        {
            for (var c = GetWindow(parent, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT))
            {
                GetWindowThreadProcessId(c, out var pid);
                if (engines.Contains(pid))
                {
                    GetWindowRect(c, out var r);
                    long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea) { bestArea = area; best = c; }
                }
                else Walk(c);
            }
        }
        Walk(_frame);
        return best;
    }

    /// <summary>
    /// While a desktop is opening, hides Citrix Workspace's own connection-progress toast ("Opening…",
    /// "Negotiating capabilities…") since Citrium shows its own progress. Only small, topmost, toast-sized
    /// windows of the Workspace tray process (Receiver.exe) are affected; its dialogs and other windows are not.
    /// </summary>
    public void HideConnectionToasts()
    {
        var receivers = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("Receiver")) using (p) receivers.Add((uint)p.Id);
        if (receivers.Count == 0) return;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!receivers.Contains(pid)) return true;
            var ex = GetWindowLongPtrW(h, -20 /* GWL_EXSTYLE */).ToInt64();
            GetWindowRect(h, out var r);
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if ((ex & 0x8 /* WS_EX_TOPMOST */) != 0 && w is > 200 and < 640 && ht is > 40 and < 220)
            {
                ShowWindow(h, SW_HIDE);
                Log.Info($"Hid Citrix Workspace connection toast ({w}x{ht})");
            }
            return true;
        }, IntPtr.Zero);
    }

    private void HideCitrixChrome()
    {
        if (_frame == IntPtr.Zero || !IsWindow(_frame) || _gaveUp) return;
        if (IsWindowVisible(_frame)) ShowWindow(_frame, SW_HIDE);

        var toolbarPids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("Citrix.EngineUI.App")) using (p) toolbarPids.Add((uint)p.Id);
        var frame = _frame;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != frame) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (toolbarPids.Contains(pid) && TextOf(h) == "MockWindow") ShowWindow(h, SW_HIDE);
            return true;
        }, IntPtr.Zero);
    }

    private bool _gaveUp;

    public void GiveUpAndShowCitrixViewer()
    {
        _gaveUp = true;
        if (_frame != IntPtr.Zero && IsWindow(_frame))
        {
            RestoreFrameStyle();
            ShowWindow(_frame, 5 /* SW_SHOW */);
        }
        Log.Info("Couldn't adopt the session surface; showing Citrix Desktop Viewer instead");
    }

    // Desktop Viewer sizes the session surface, and negotiates the session's resolution, from its own client
    // area. So the (hidden) frame's client area is kept equal to our container: that's what makes the remote
    // desktop follow Citrium's window (resize, maximize, full screen). Its caption/borders are removed while
    // hidden so its client can match any size, full screen included (a captioned window can't grow past the
    // screen). Resizes are debounced and only issued when our size actually changes: every resolution change
    // makes Citrix renegotiate the display, so it must never be done in a loop.

    private long _frameOriginalStyle;
    private (int W, int H) _syncTarget;
    private (int W, int H) _pendingTarget;
    private DateTime _pendingSince;
    private static readonly TimeSpan ResizeSettle = TimeSpan.FromMilliseconds(350);

    /// <summary>Removes the frame's caption and borders. Window-state changes can bring them back, so this is repeated.</summary>
    private void StripFrameDecorations()
    {
        if (_frame == IntPtr.Zero || !IsWindow(_frame)) return;
        var current = GetWindowLongPtrW(_frame, GWL_STYLE).ToInt64();
        if ((current & (WS_CAPTION | WS_THICKFRAME)) == 0) return;
        if (_frameOriginalStyle == 0) _frameOriginalStyle = current;
        SetWindowLongPtrW(_frame, GWL_STYLE, new IntPtr(current & ~(WS_CAPTION | WS_THICKFRAME)));
        SetWindowPos(_frame, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Desktop Viewer remembers being maximized between sessions, and a maximized frame ignores the size sync:
    /// the session keeps its full-screen resolution and is cropped by Citrium's window. Restoring it needs a real
    /// window-state change (the style flag alone leaves Desktop Viewer maximized), so it's shown for an instant
    /// and hidden again; the foreground watchdog hands the keyboard straight back.
    /// </summary>
    private void ClearFrameMaximized()
    {
        if (_frame == IntPtr.Zero || !IsWindow(_frame) || !IsZoomed(_frame)) return;
        ShowWindow(_frame, SW_RESTORE);
        ShowWindow(_frame, SW_HIDE);
        Log.Info("Restored Citrix frame from maximized");
    }

    private void RestoreFrameStyle()
    {
        if (_frameOriginalStyle == 0 || _frame == IntPtr.Zero || !IsWindow(_frame)) return;
        SetWindowLongPtrW(_frame, GWL_STYLE, new IntPtr(_frameOriginalStyle));
        SetWindowPos(_frame, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        _frameOriginalStyle = 0;
    }

    private const int MaxSyncAttempts = 4;
    private static readonly TimeSpan SyncRetryDelay = TimeSpan.FromMilliseconds(800);
    private (int W, int H) _attemptTarget;
    private int _syncAttempts;
    private DateTime _nextSyncAttempt;

    private void SyncFrameSize()
    {
        if (_frame == IntPtr.Zero || !IsWindow(_frame) || _gaveUp || !IsAdopted) return;
        GetClientRect(Container, out var want);
        var target = (W: want.Right - want.Left, H: want.Bottom - want.Top);
        if (target.W < 64 || target.H < 64 || target == _syncTarget) return;

        if (target != _pendingTarget) { _pendingTarget = target; _pendingSince = DateTime.UtcNow; return; }
        if (DateTime.UtcNow - _pendingSince < ResizeSettle) return;

        // The frame may not take the size at once (it can still be un-maximizing), so check and retry a few
        // times, well spaced. Capped: repeated resizes make Citrix renegotiate the display.
        if (target != _attemptTarget) { _attemptTarget = target; _syncAttempts = 0; _nextSyncAttempt = default; }
        if (DateTime.UtcNow < _nextSyncAttempt) return;

        ClearFrameMaximized();
        StripFrameDecorations();
        GetClientRect(_frame, out var have);
        GetWindowRect(_frame, out var outer);
        int dw = (outer.Right - outer.Left) - (have.Right - have.Left);
        int dh = (outer.Bottom - outer.Top) - (have.Bottom - have.Top);
        SetWindowPos(_frame, IntPtr.Zero, 0, 0, target.W + dw, target.H + dh, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        GetClientRect(_frame, out var now);

        _syncAttempts++;
        _nextSyncAttempt = DateTime.UtcNow + SyncRetryDelay;
        var matched = now.Right == target.W && now.Bottom == target.H;
        if (matched || _syncAttempts >= MaxSyncAttempts) _syncTarget = target;
        _lastSizedAt = DateTime.UtcNow;
        Log.Info($"Session resized to {target.W}x{target.H} (Citrix frame client now {now.Right}x{now.Bottom}, attempt {_syncAttempts})");
    }

    private const int MaxSizeNudges = 3;
    private const int SizeTolerance = 24; // Citrix keeps a small margin in full screen; that's not a mismatch
    private static readonly TimeSpan SessionSizeGrace = TimeSpan.FromSeconds(4);
    private DateTime _lastSizedAt;
    private (int W, int H) _nudgeTarget;
    private int _sizeNudges;

    private string _lastWindowReport = "";

    /// <summary>
    /// Citrix can move the surface inside its parent (seen as a session opens, when it restores a maximized Desktop
    /// Viewer), which leaves the desktop shifted and cut off in the window. Moving it back doesn't touch the
    /// resolution, so it's always safe.
    /// </summary>
    private void PullSurfaceHome()
    {
        GetWindowRect(_surface, out var surface);
        GetWindowRect(Container, out var container);
        int x = surface.Left - container.Left, y = surface.Top - container.Top;
        if (Math.Abs(x) <= SizeTolerance && Math.Abs(y) <= SizeTolerance) return;

        Log.Info($"Session surface drifted to {x},{y}; moving it back");
        SetWindowPos(_surface, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void ReportSessionWindows()
    {
        string Describe(IntPtr parent)
        {
            var parts = new List<string>();
            EnumChildWindows(parent, (h, _) =>
            {
                GetWindowRect(h, out var r);
                parts.Add($"{ClassOf(h)}:{r.Right - r.Left}x{r.Bottom - r.Top}{(IsWindowVisible(h) ? "" : ":hidden")}");
                return true;
            }, IntPtr.Zero);
            return string.Join(' ', parts);
        }

        var report = $"surface=[{Describe(_surface)}] frame({(IsWindowVisible(_frame) ? "shown" : "hidden")})=[{Describe(_frame)}]";
        if (report == _lastWindowReport) return;
        _lastWindowReport = report;
        Log.Info($"Citrix windows: {report}");
    }

    /// <summary>
    /// A resize that reaches Desktop Viewer before the session is ready is dropped: the frame ends up the right
    /// size but the session keeps its old resolution and is cropped by the window, with nothing left to ask
    /// again. So check the session itself, and if it hasn't followed, change the frame slightly so the next
    /// sync is a fresh resize. Capped, because every resolution change makes Citrix renegotiate the display.
    /// </summary>
    private void VerifySessionSize()
    {
        if (_syncTarget == default || _surface == IntPtr.Zero || !IsWindow(_surface) || !IsAdopted) return;
        PullSurfaceHome();
        ReportSessionWindows();
        if (DateTime.UtcNow - _lastSizedAt < SessionSizeGrace) return;

        GetWindowRect(_surface, out var surface);
        int w = surface.Right - surface.Left, h = surface.Bottom - surface.Top;
        if (w == 0 || h == 0) return;
        if (_nudgeTarget != _syncTarget) { _nudgeTarget = _syncTarget; _sizeNudges = 0; }
        if (Math.Abs(w - _syncTarget.W) <= SizeTolerance && Math.Abs(h - _syncTarget.H) <= SizeTolerance)
        {
            _sizeNudges = 0;
            return;
        }
        if (_sizeNudges >= MaxSizeNudges) return;

        _sizeNudges++;
        Log.Info($"Session is {w}x{h}, expected {_syncTarget.W}x{_syncTarget.H}: asking again ({_sizeNudges}/{MaxSizeNudges})");
        RequestFreshResize();
    }

    /// <summary>Citrix drops a resize that doesn't change the frame's size, so step off the real size and back in one call.</summary>
    private void RequestFreshResize()
    {
        var target = _syncTarget;
        GetClientRect(_frame, out var have);
        GetWindowRect(_frame, out var outer);
        int dw = (outer.Right - outer.Left) - (have.Right - have.Left);
        int dh = (outer.Bottom - outer.Top) - (have.Bottom - have.Top);
        int width = target.W + dw, height = target.H + dh;
        SetWindowPos(_frame, IntPtr.Zero, 0, 0, width, Math.Max(64, height - 2), SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(_frame, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        _syncTarget = target;
        _pendingTarget = target;
        _pendingSince = DateTime.UtcNow;
        _attemptTarget = target;
        _lastSizedAt = DateTime.UtcNow;
    }

    private void FitSurface(bool force = false)
    {
        SyncFrameSize();
        VerifySessionSize();
        if (_surface == IntPtr.Zero || !IsWindow(_surface)) return;
        if (force)
        {
            // Initial placement only. From then on Desktop Viewer sizes the surface from its (synced) frame;
            // resizing it here as well would fight Citrix and trigger display renegotiation.
            GetClientRect(Container, out var target);
            SetWindowPos(_surface, HWND_TOP, 0, 0, target.Right - target.Left, target.Bottom - target.Top,
                SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        }
    }

    private static bool HasExited(Process p) { try { return p.HasExited; } catch { return true; } }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    private static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    private static string TextOf(IntPtr h) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }

    private static void SendKeys(params (int Vk, bool Up)[] keys)
    {
        var inputs = keys.Select(k => new INPUT
        {
            type = 1, // INPUT_KEYBOARD
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)k.Vk,
                    wScan = (ushort)MapVirtualKey((uint)k.Vk, 0),
                    dwFlags = k.Up ? 2u /* KEYEVENTF_KEYUP */ : 0u,
                },
            },
        }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Container != IntPtr.Zero) DestroyWindow(Container);
        Engine?.Dispose();
    }

    private const int GWL_STYLE = -16, SW_HIDE = 0, SW_SHOWNA = 8, SW_RESTORE = 9;
    private const uint GA_ROOT = 2;
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000, SS_BLACKRECT = 0x4;
    private const long WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_FRAMECHANGED = 0x0020, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOSIZE = 0x0001;
    private const uint GW_HWNDNEXT = 2, GW_OWNER = 4, GW_CHILD = 5;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    private const uint WM_CLOSE = 0x0010;
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
}
