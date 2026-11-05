using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Citrium.Viewer;
using Microsoft.UI.Dispatching;

namespace Citrium.Services;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class HdxSession : INotifyPropertyChanged
{
    public required int ProcessId { get; init; }
    public required string Name { get; set; }
    public string? AccountName { get; init; }
    public DateTime Started { get; init; }
    public bool IsDesktop { get; init; }
    public string Glyph => IsDesktop ? "" : "";

    private string _title = "";
    public string WindowTitle
    {
        get => _title;
        set { if (_title == value) return; _title = value; OnChanged(); OnChanged(nameof(Detail)); }
    }

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(AccountName)) parts.Add(AccountName);
            parts.Add($"Started {Started:t}");
            if (!string.IsNullOrEmpty(WindowTitle) && WindowTitle != Name) parts.Add(WindowTitle);
            return string.Join(" · ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

/// <summary>
/// Tracks running HDX engine processes (each wfica32.exe instance is one ICA session), including ones
/// that were started outside Citrium.
/// </summary>
public sealed class SessionMonitor
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<int, (string Name, string? Account, bool Desktop)> _known = [];

    public ObservableCollection<HdxSession> Sessions { get; } = [];

    public SessionMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    public void Register(Process process, string name, string? account, bool isDesktop)
    {
        _known[process.Id] = (name, account, isDesktop);
        _dispatcher.TryEnqueue(Poll);
    }

    public void Poll()
    {
        List<Process> procs;
        try { procs = [.. Process.GetProcessesByName("wfica32")]; } catch { return; }

        // Engines displayed inside a Citrium viewer are listed once, as the viewer — including ones whose
        // window is hidden, and ones that outlived a restart of the main window.
        var viewers = ViewerRegistry.ReadAll();
        foreach (var viewer in viewers)
            _known.TryAdd(viewer.ViewerPid, (viewer.Title, viewer.Account, true));

        // Only Citrium viewer processes. A desktop opened in Citrix's own viewer is also marked desktop,
        // and its engine must stay in the list.
        var viewerPids = viewers.Select(v => v.ViewerPid).ToHashSet();
        foreach (var pid in _known.Where(kv => kv.Value.Desktop).Select(kv => kv.Key))
            if (IsCitriumProcess(pid)) viewerPids.Add(pid);
        var adopted = viewers.Where(v => v.EnginePid != 0).Select(v => v.EnginePid).ToHashSet();
        foreach (var p in procs.ToList())
        {
            if (adopted.Contains(p.Id) || HostedByViewer(p.Id, viewerPids))
            {
                procs.Remove(p);
                p.Dispose();
            }
        }

        // Plus Citrium viewer processes (they keep the session when their window is hidden).
        foreach (var pid in _known.Keys.ToList())
        {
            if (procs.Any(p => p.Id == pid)) continue;
            try
            {
                var p = Process.GetProcessById(pid);
                if (!p.HasExited) { procs.Add(p); continue; }
                p.Dispose();
            }
            catch (ArgumentException) { }
            _known.Remove(pid);
        }

        var alive = new HashSet<int>();
        foreach (var p in procs)
        {
            using (p)
            {
                alive.Add(p.Id);
                string title = "";
                try { title = p.MainWindowTitle; } catch { }

                var existing = Sessions.FirstOrDefault(s => s.ProcessId == p.Id);
                if (existing is not null)
                {
                    existing.WindowTitle = title;
                    continue;
                }

                DateTime started;
                try { started = p.StartTime; } catch { started = DateTime.Now; }

                var info = _known.TryGetValue(p.Id, out var k) ? k : (Name: string.IsNullOrEmpty(title) ? "Citrix session" : title, Account: (string?)null, Desktop: false);
                Sessions.Add(new HdxSession
                {
                    ProcessId = p.Id,
                    Name = info.Name,
                    AccountName = info.Account,
                    IsDesktop = info.Desktop,
                    Started = started,
                    WindowTitle = title,
                });
            }
        }

        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            if (!alive.Contains(Sessions[i].ProcessId))
            {
                _known.Remove(Sessions[i].ProcessId);
                Sessions.RemoveAt(i);
            }
        }
    }

    private static bool IsCitriumProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("Citrium", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool HostedByViewer(int pid, HashSet<int> viewerPids)
    {
        var parent = ParentPid(pid);
        return parent != 0 && viewerPids.Contains(parent);
    }

    private static int ParentPid(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var info = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            return status == 0 ? unchecked((int)info.InheritedFromUniqueProcessId.ToInt64()) : 0;
        }
        catch { return 0; }
        finally { CloseHandle(handle); }
    }

    public static void BringToFront(HdxSession session)
    {
        // A hidden viewer has no taskbar window; it shows itself when asked.
        if (ViewerRegistry.FindByPid(session.ProcessId) is { } viewer)
        {
            ViewerRegistry.Show(viewer);
            return;
        }
        try
        {
            using var p = Process.GetProcessById(session.ProcessId);
            var hwnd = p.MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
            SetForegroundWindow(hwnd);
        }
        catch { /* process gone */ }
    }

    /// <summary>Disconnects the session by closing the local engine. The server-side session keeps running
    /// (per the site's disconnect policy) and can be reconnected by launching the resource again.</summary>
    public void Disconnect(HdxSession session)
    {
        if (ViewerRegistry.FindByPid(session.ProcessId) is { } viewer)
        {
            // Closing the viewer's window would only hide it. Ask it to disconnect, and force it if it sticks.
            ViewerRegistry.RequestDisconnect(viewer.ViewerPid);
            var enginePid = viewer.EnginePid;
            _ = Task.Run(() =>
            {
                try
                {
                    using var p = Process.GetProcessById(viewer.ViewerPid);
                    if (!p.WaitForExit(6000))
                    {
                        EndEngine(enginePid);
                        try { p.Kill(entireProcessTree: true); } catch { }
                    }
                }
                catch (ArgumentException) { /* already gone */ }
                _dispatcher.TryEnqueue(Poll);
            });
            return;
        }

        try
        {
            using var p = Process.GetProcessById(session.ProcessId);
            if (!p.CloseMainWindow() || !p.WaitForExit(4000)) p.Kill();
        }
        catch { /* already gone */ }
        Poll();
    }

    private static void EndEngine(int pid)
    {
        if (pid == 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.ProcessName.Equals("wfica32", StringComparison.OrdinalIgnoreCase)) p.Kill();
        }
        catch { /* already gone */ }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, ref ProcessBasicInformation info, int length, out int returnLength);
}
