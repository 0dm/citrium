using System.Diagnostics;
using System.Runtime.InteropServices;
using Citrium.Services;

namespace Citrium.Viewer;

public sealed record ViewerInfo(int ViewerPid, int EnginePid, long WindowHandle, string Title, string? Account);

/// <summary>
/// How the main window and viewer processes find and talk to each other. Each viewer registers itself in
/// a small file (so Citrium can list it even while its window is hidden, or after Citrium restarts); named
/// events carry the "disconnect" requests; a named mutex tells viewers whether the main app is running.
/// </summary>
internal static class ViewerRegistry
{
    private const string MainMutexName = @"Local\Citrium.Main.Running";
    private const string DisconnectAllEventName = @"Local\Citrium.Viewers.DisconnectAll";
    private static string DisconnectEventName(int viewerPid) => $@"Local\Citrium.Viewer.Disconnect.{viewerPid}";
    private static string ShowEventName(int viewerPid) => $@"Local\Citrium.Viewer.Show.{viewerPid}";
    private static string FileFor(int viewerPid) => Path.Combine(AppPaths.Viewers, $"{viewerPid}.viewer");

    private static Mutex? _mainMutex;

    /// <summary>Held for the lifetime of the main app. Also clears any leftover "disconnect all" request.</summary>
    public static void MarkMainRunning()
    {
        _mainMutex = new Mutex(false, MainMutexName);
        using var all = new EventWaitHandle(false, EventResetMode.ManualReset, DisconnectAllEventName);
        all.Reset();
    }

    public static void RequestDisconnect(int viewerPid)
    {
        using var e = new EventWaitHandle(false, EventResetMode.ManualReset, DisconnectEventName(viewerPid));
        e.Set();
    }

    public static void RequestDisconnectAll()
    {
        using var e = new EventWaitHandle(false, EventResetMode.ManualReset, DisconnectAllEventName);
        e.Set();
    }

    public static List<ViewerInfo> ReadAll()
    {
        var result = new List<ViewerInfo>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.Viewers, "*.viewer"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var viewerPid)) continue;
                if (!IsAlive(viewerPid)) { try { File.Delete(file); } catch { } continue; }
                var lines = File.ReadAllLines(file);
                if (lines.Length < 3) continue;
                if (!int.TryParse(lines[0], out var enginePid) || !long.TryParse(lines[1], out var hwnd)) continue;
                result.Add(new ViewerInfo(viewerPid, enginePid, hwnd, lines[2],
                    lines.Length > 3 && lines[3].Length > 0 ? lines[3] : null));
            }
        }
        catch (Exception ex) { Log.Error("Couldn't read viewer registrations", ex); }
        return result;
    }

    public static ViewerInfo? Find(string title, string? account) =>
        ReadAll().FirstOrDefault(v => v.Title == title && v.Account == account);

    public static ViewerInfo? FindByPid(int viewerPid) =>
        ReadAll().FirstOrDefault(v => v.ViewerPid == viewerPid);

    /// <summary>
    /// Asks the viewer to show its window again. The viewer does the actual show on its own thread, because a
    /// window hidden with <c>AppWindow.Hide</c> only comes back reliably from inside that process.
    /// </summary>
    public static void Show(ViewerInfo viewer)
    {
        try { AllowSetForegroundWindow(viewer.ViewerPid); } catch { /* best effort */ }
        using var e = new EventWaitHandle(false, EventResetMode.ManualReset, ShowEventName(viewer.ViewerPid));
        e.Set();
    }

    public static bool IsMainRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MainMutexName, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch { return false; }
    }

    public static void Register(ViewerInfo info)
    {
        try
        {
            File.WriteAllLines(FileFor(info.ViewerPid),
                [info.EnginePid.ToString(), info.WindowHandle.ToString(), info.Title, info.Account ?? ""]);
        }
        catch (Exception ex) { Log.Error("Couldn't register viewer", ex); }
    }

    public static void Unregister(int viewerPid)
    {
        try { File.Delete(FileFor(viewerPid)); } catch { }
    }

    public static (EventWaitHandle Mine, EventWaitHandle All, EventWaitHandle Show) OpenSignals(int viewerPid) => (
        new EventWaitHandle(false, EventResetMode.ManualReset, DisconnectEventName(viewerPid)),
        new EventWaitHandle(false, EventResetMode.ManualReset, DisconnectAllEventName),
        new EventWaitHandle(false, EventResetMode.ManualReset, ShowEventName(viewerPid)));

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
