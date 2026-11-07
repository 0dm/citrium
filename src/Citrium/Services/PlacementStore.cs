using System.Text.Json;
using Citrium.Models;

namespace Citrium.Services;

/// <summary>
/// Window positions (placements.json), apart from viewer.json because every viewer process saves these as windows
/// move, and each viewer keeps its own copy of its settings. Every change here is read, changed and written under
/// a lock, so one viewer can't put back another's old position.
/// </summary>
internal static class PlacementStore
{
    private static string FilePath => Path.Combine(AppPaths.Root, "placements.json");
    private static readonly Mutex Gate = new(false, $@"Local\{AppPaths.InstanceName}.Placements");

    public static Placements Load() => Locked(Read);

    public static void Update(Action<Placements> change) => Locked(() =>
    {
        var placements = Read();
        change(placements);
        try
        {
            var tmp = $"{FilePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(placements, JsonContext.Default.Placements));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save window positions", ex);
        }
        return placements;
    });

    private static Placements Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), JsonContext.Default.Placements) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read window positions", ex);
        }
        return new Placements();
    }

    private static T Locked<T>(Func<T> action)
    {
        var held = false;
        try
        {
            try { held = Gate.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { held = true; } // a viewer ended mid-save; the file itself is written atomically
            return action();
        }
        finally
        {
            if (held) Gate.ReleaseMutex();
        }
    }
}
