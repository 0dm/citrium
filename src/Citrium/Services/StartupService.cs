using Microsoft.Win32;

namespace Citrium.Services;

/// <summary>Citrium's footprint in Windows: starting at sign-in, the citrium:// scheme and desktop shortcuts.</summary>
internal static class StartupService
{
    public const string BackgroundArgument = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Citrium";

    private static string Exe => Environment.ProcessPath!;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
    }

    public static void SetEnabled(bool on)
    {
        if (AppPaths.IsDemo) return; // a demo leaves no trace in Windows

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Exe}\" {BackgroundArgument}");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>
    /// Points citrium:// and, if it's on, startup at this copy of the program. Run on every start because Citrium
    /// isn't installed: nothing else would notice the folder moving.
    /// </summary>
    public static void Refresh()
    {
        if (AppPaths.IsDemo) return;

        try
        {
            using var scheme = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{CitriumLink.Scheme}");
            scheme.SetValue("", "URL:Citrium");
            scheme.SetValue("URL Protocol", "");
            using (var icon = scheme.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{Exe}\",0");
            using (var command = scheme.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{Exe}\" \"%1\"");
            if (IsEnabled) SetEnabled(true);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't register with Windows", ex);
        }
    }

    /// <summary>
    /// An Internet Shortcut rather than a .lnk: it's a small text file that goes through citrium:// like every
    /// other way in, and it needs no COM. Returns its path.
    /// </summary>
    public static string CreateDesktopShortcut(string name, CitriumLink link)
    {
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "Citrium";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{safe}.url");
        File.WriteAllLines(path, ["[InternetShortcut]", $"URL={link.ToUri()}", $"IconFile={Exe}", "IconIndex=0"]);
        return path;
    }
}
