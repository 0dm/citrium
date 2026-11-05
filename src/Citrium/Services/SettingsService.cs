using System.Text.Json;
using Citrium.Models;

namespace Citrium.Services;

internal static class SettingsService
{
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), JsonContext.Default.AppSettings) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read settings", ex);
        }
        return new AppSettings();
    }

    private static string ViewerFile => Path.Combine(AppPaths.Root, "viewer.json");

    public static ViewerSettings LoadViewer()
    {
        try
        {
            if (File.Exists(ViewerFile))
                return JsonSerializer.Deserialize(File.ReadAllText(ViewerFile), JsonContext.Default.ViewerSettings) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read viewer settings", ex);
        }
        return new ViewerSettings();
    }

    public static void SaveViewer(ViewerSettings settings)
    {
        try
        {
            var tmp = ViewerFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonContext.Default.ViewerSettings));
            File.Move(tmp, ViewerFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save viewer settings", ex);
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonContext.Default.AppSettings));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }
}
