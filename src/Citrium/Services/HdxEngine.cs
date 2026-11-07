using System.Diagnostics;
using Microsoft.Win32;

namespace Citrium.Services;

/// <summary>
/// Locates and drives the Citrix HDX engine (wfica32.exe). The ICA/HDX protocol is proprietary and has no
/// open-source implementation, so Citrium uses the engine installed with Citrix Workspace app (the
/// "ICA Client" component) for the actual remoting, and replaces everything around it.
/// </summary>
internal static class HdxEngine
{
#if DEMO
    public const string ProcessName = "citrium-demo-machine";
#else
    public const string ProcessName = "wfica32";
#endif

    public static string? Locate(string? overridePath = null)
    {
#if DEMO
        return Path.Combine(AppContext.BaseDirectory, "DemoMachine", ProcessName + ".exe");
#else
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Citrix\ICA Client");
                    if (key?.GetValue("InstallFolder") is string folder)
                    {
                        var exe = Path.Combine(folder, "wfica32.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch { /* keep looking */ }
            }
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Citrix\ICA Client\wfica32.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Citrix\ICA Client\wfica32.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Citrix\ICA Client\wfica32.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
#endif
    }

    public static string? Version(string? path)
    {
        if (path is null) return null;
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; } catch { return null; }
    }

    public static Process Launch(string icaContent, string displayName, string? overridePath, bool keepFile)
    {
        var engine = Locate(overridePath) ?? throw new FileNotFoundException(
            "The Citrix HDX engine (wfica32.exe) was not found. Install the Citrix Workspace app (only its " +
            "HDX engine is used) or set its location in Settings.");

        var safe = string.Concat(displayName.Split(Path.GetInvalidFileNameChars()));
        var file = Path.Combine(AppPaths.IcaFiles, $"{safe}-{Guid.NewGuid():N}.ica");
        File.WriteAllText(file, icaContent);
        return LaunchFile(file, engine, deleteAfter: !keepFile);
    }

    public static Process LaunchFile(string icaFile, string? engine = null, bool deleteAfter = false)
    {
        engine ??= Locate() ?? throw new FileNotFoundException("The Citrix HDX engine (wfica32.exe) was not found.");
        var psi = new ProcessStartInfo(engine)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(engine)!,
        };
        psi.ArgumentList.Add(icaFile);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the HDX engine.");
        Log.Info($"Started {engine} (pid {proc.Id}) for {icaFile}");

        if (deleteAfter)
        {
            // The engine reads the file immediately; the ICA logon ticket inside is single-use anyway.
            _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ =>
            {
                try { File.Delete(icaFile); } catch { /* still open, leave it */ }
            });
        }
        return proc;
    }

    public static void CleanupOldFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.IcaFiles, "*.ica"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) File.Delete(f);
        }
        catch { /* ignore */ }
    }
}
