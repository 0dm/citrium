namespace Citrium.Services;

internal static class AppPaths
{
#if DEMO
    /// <summary>Names this copy's data folder and kernel objects, so a demo can run beside the real thing.</summary>
    public const string InstanceName = "Citrium Demo";
    public static bool IsDemo => true;
#else
    public const string InstanceName = "Citrium";
    public static bool IsDemo => false;
#endif

    public static string Root { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstanceName));

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string Sessions => Ensure(Path.Combine(Root, "Sessions"));
    public static string Icons => Ensure(Path.Combine(Root, "Icons"));
    public static string IcaFiles => Ensure(Path.Combine(Root, "Ica"));
    public static string WebView2Data => Ensure(Path.Combine(Root, "WebView2"));
    public static string Viewers => Ensure(Path.Combine(Root, "Viewers"));
    public static string Updates => Ensure(Path.Combine(Root, "Updates"));
    public static string LogFile => Path.Combine(Root, "citrium.log");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 2 * 1024 * 1024) file.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTimeOffset.Now:u} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
