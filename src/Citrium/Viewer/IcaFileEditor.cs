using System.Text;
using Citrium.Services;

namespace Citrium.Viewer;

internal static class IcaFileEditor
{
    public static void SetValue(string path, string section, string key, string value)
    {
        try
        {
            var lines = File.ReadAllLines(path).ToList();
            var header = $"[{section}]";
            var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                lines.Insert(0, header);
                lines.Insert(1, $"{key}={value}");
            }
            else
            {
                var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
                if (end < 0) end = lines.Count;
                var existing = lines.FindIndex(start + 1, end - start - 1,
                    l => l.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) lines[existing] = $"{key}={value}";
                else lines.Insert(start + 1, $"{key}={value}");
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't set {key} in the launch file", ex);
        }
    }

    /// <summary>
    /// Sets <paramref name="key"/> for the session being launched. A store can put the same key in the resource's
    /// own section (the ones listed under <c>[ApplicationServers]</c>), which beats <c>[WFClient]</c>, so for keys
    /// a store may send, the value has to go in both places or the store's would silently win.
    /// </summary>
    public static void SetSessionValue(string path, string key, string value)
    {
        SetValue(path, "WFClient", key, value);
        foreach (var section in ResourceSections(path)) SetValue(path, section, key, value);
    }

    private static List<string> ResourceSections(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var start = Array.FindIndex(lines, l => l.Trim().Equals("[ApplicationServers]", StringComparison.OrdinalIgnoreCase));
            if (start < 0) return [];
            return lines.Skip(start + 1)
                .TakeWhile(l => !l.TrimStart().StartsWith('['))
                .Select(l => l.Split('=')[0].Trim())
                .Where(name => name.Length > 0 && lines.Any(l => l.Trim().Equals($"[{name}]", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read the launch file's resources", ex);
            return [];
        }
    }

    public static void RemoveValue(string path, string section, string key)
    {
        try
        {
            var lines = File.ReadAllLines(path).ToList();
            var header = $"[{section}]";
            var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (start < 0) return;
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            for (var i = end - 1; i > start; i--)
                if (lines[i].TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(i);
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't remove {key} from the launch file", ex);
        }
    }
}
