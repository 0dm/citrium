using System.Diagnostics;
using Citrium.Services;

namespace Citrium.Viewer;

/// <summary>
/// Starts a Citrium viewer for a desktop. Each session gets its own process (Citrium.exe --viewer …), just as
/// Citrix runs one wfica32.exe per session, so a crash in one session's engine can't take down Citrium or
/// other sessions.
/// </summary>
internal static class ViewerLauncher
{
    public static bool IsAvailable(out string reason)
    {
        if (HdxEngine.Locate(AppState.Current.Settings.HdxEnginePath) is not null)
        {
            reason = "";
            return true;
        }
        reason = "Needs Citrix Workspace app, which provides the engine that displays desktops.";
        return false;
    }

    public static Process Launch(string icaContent, string title, string? account, bool keepFile)
    {
        var safe = string.Concat(title.Split(Path.GetInvalidFileNameChars()));
        var file = Path.Combine(AppPaths.IcaFiles, $"{safe}-{Guid.NewGuid():N}.ica");
        File.WriteAllText(file, icaContent);

        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        psi.ArgumentList.Add("--viewer");
        psi.ArgumentList.Add(file);
        psi.ArgumentList.Add("--title");
        psi.ArgumentList.Add(title);
        if (!string.IsNullOrEmpty(account))
        {
            psi.ArgumentList.Add("--account");
            psi.ArgumentList.Add(account);
        }
        if (!keepFile) psi.ArgumentList.Add("--delete-ica");

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the Citrium viewer.");
        Log.Info($"Started Citrium viewer (pid {proc.Id}) for {title}");
        return proc;
    }
}
